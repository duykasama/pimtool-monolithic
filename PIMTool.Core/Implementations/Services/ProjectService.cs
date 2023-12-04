﻿using System.Data;
using System.Linq.Expressions;
using System.Text;
using Autofac;
using AutoMapper;
using AutoMapper.QueryableExtensions;
using ExcelDataReader;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json;
using NPOI.HSSF.Record;
using NPOI.HSSF.Record.Chart;
using NPOI.HSSF.UserModel;
using NPOI.HSSF.Util;
using NPOI.SS.UserModel;
using NPOI.Util.ArrayExtensions;
using NPOI.XSSF.UserModel;
using PIMTool.Core.Constants;
using PIMTool.Core.Domain.Entities;
using PIMTool.Core.Exceptions;
using PIMTool.Core.Helpers;
using PIMTool.Core.Implementations.Services.Base;
using PIMTool.Core.Interfaces.Repositories;
using PIMTool.Core.Interfaces.Services;
using PIMTool.Core.Models;
using PIMTool.Core.Models.Dtos;
using PIMTool.Core.Models.Request;
using SixLabors.ImageSharp;

namespace PIMTool.Core.Implementations.Services;

public class ProjectService : BaseService, IProjectService
{
    private readonly IProjectRepository _projectRepository;
    private readonly IGroupRepository _groupRepository;
    private readonly IPIMUserRepository _userRepository;
    private readonly IEmployeeRepository _employeeRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IMapper _mapper;
    
    public ProjectService(ILifetimeScope scope) : base(scope)
    {
        _projectRepository = Resolve<IProjectRepository>();
        _groupRepository = Resolve<IGroupRepository>();
        _userRepository = Resolve<IPIMUserRepository>();
        _employeeRepository = Resolve<IEmployeeRepository>();
        _unitOfWork = Resolve<IUnitOfWork>();
        _mapper = Resolve<IMapper>();
    }

    public async Task<ApiActionResult> GetAllProjectsAsync()
    {
        var projects = await _projectRepository.FindByAsync(p => !p.IsDeleted);
        var projectDtos = _mapper.Map<IEnumerable<DtoProject>>(await projects.ToListAsync());
        return new ApiActionResult(true) { Data = projectDtos };
    }

    public async Task<ApiActionResult> FindProjectsAsync(SearchProjectsRequest req)
    {
        var projects = await _projectRepository
            .FindByAsync(e => !e.IsDeleted).ConfigureAwait(false);

        if (req.SearchCriteria is not null)
        {
            Expression<Func<Project, bool>> conjunctionExpr = project => false;

            foreach (var searchInfo in req.SearchCriteria.ConjunctionSearchInfos)
            {
                var valueStr = searchInfo.Value.ToString()?.Trim();
                conjunctionExpr = searchInfo.FieldName switch
                {
                    "projectNumber" => ExpressionHelper.CombineOrExpressions(conjunctionExpr,
                        (Expression<Func<Project, bool>>)(project =>
                            EF.Functions.Like(project.ProjectNumber.ToString(), $"%{valueStr}%"))),
                    "name" => ExpressionHelper.CombineOrExpressions(conjunctionExpr,
                        (Expression<Func<Project, bool>>)(project => 
                            EF.Functions.Like(project.Name, $"%{valueStr}%"))),
                    "customer" => ExpressionHelper.CombineOrExpressions(conjunctionExpr,
                        (Expression<Func<Project, bool>>)(project =>
                            EF.Functions.Like(project.Customer, $"%{valueStr}%"))),
                    _ => conjunctionExpr
                };
            }
            
            projects = req.SearchCriteria.ConjunctionSearchInfos.IsNullOrEmpty() 
                ? projects 
                : projects.Where(conjunctionExpr);

            foreach (var searchInfo in req.SearchCriteria.DisjunctionSearchInfos)
            {
                var valueStr = searchInfo.Value.ToString()!.Trim();
                projects = searchInfo.FieldName switch
                {
                    "status" => projects.Where(p => EF.Functions.Like(p.Status, $"%{valueStr}%")),
                    _ => projects
                };
            }
        }

        if (req.AdvancedFilter is not null)
        {
            projects = projects
                .Include(p => p.Group.Leader)
                .Where(p => 
                    EF.Functions.Like(p.Group.Leader.FirstName, $"%{req.AdvancedFilter.LeaderName}%") ||
                    EF.Functions.Like(p.Group.Leader.LastName, $"%{req.AdvancedFilter.LeaderName}%"));
            
            if (req.AdvancedFilter.StartDateRange?.From != null)
            {
                projects = projects.Where(p => p.StartDate >= req.AdvancedFilter.StartDateRange.From);
            }
            if (req.AdvancedFilter.StartDateRange?.To != null)
            {
                projects = projects.Where(p => p.StartDate <= req.AdvancedFilter.StartDateRange.To);
            }
            
            if (req.AdvancedFilter.EndDateRange?.From != null)
            {
                projects = projects.Where(p => p.EndDate != null && p.EndDate >= req.AdvancedFilter.EndDateRange.From);
            }
            if (req.AdvancedFilter.EndDateRange?.To != null)
            {
                projects = projects.Where(p => p.EndDate != null && p.EndDate <= req.AdvancedFilter.EndDateRange.To);
            }
        }

        var orderedProjects = projects.OrderBy(p => "");
        if (req.SortByInfos is not null)
        {
            foreach (var sortInfo in req.SortByInfos)
            {
                orderedProjects = sortInfo.FieldName switch
                {
                    "projectNumber" => sortInfo.Ascending
                        ? orderedProjects.ThenBy(p => p.ProjectNumber)
                        : orderedProjects.ThenByDescending(p => p.ProjectNumber),
                    "name" => sortInfo.Ascending
                        ? orderedProjects.ThenBy(p => p.Name)
                        : orderedProjects.ThenByDescending(p => p.Name),
                    "status" => sortInfo.Ascending
                        ? orderedProjects.ThenBy(p => p.Status)
                        : orderedProjects.ThenByDescending(p => p.Status),
                    "customer" => sortInfo.Ascending
                        ? orderedProjects.ThenBy(p => p.Customer)
                        : orderedProjects.ThenByDescending(p => p.Customer),
                    "startDate" => sortInfo.Ascending
                        ? orderedProjects.ThenBy(p => p.StartDate)
                        : orderedProjects.ThenByDescending(p => p.StartDate),
                    _ => orderedProjects
                };
            }
        }

        var paginatedResult = await PaginationHelper.BuildPaginatedResult(orderedProjects.ProjectTo<DtoProject>(_mapper.ConfigurationProvider),
                req.PageSize, req.PageIndex).ConfigureAwait(false);
        
        return new ApiActionResult(true) { Data = paginatedResult };
    }

    public async Task<ApiActionResult> CreateProjectAsync(CreateProjectRequest createProjectRequest)
    {
        if (await _projectRepository.ExistsAsync(p => !p.IsDeleted && p.ProjectNumber == createProjectRequest.ProjectNumber).ConfigureAwait(false))
        {
            throw new ProjectNumberAlreadyExistsException();
        }

        if (!await _groupRepository.ExistsAsync(g => g.Id == createProjectRequest.GroupId && !g.IsDeleted))
        {
            throw new GroupDoesNotExistException();
        }

        var existingProject = await _projectRepository
            .GetAsync(p => p.ProjectNumber == createProjectRequest.ProjectNumber && p.IsDeleted).ConfigureAwait(false);
        if (existingProject is not null)
        {
            await _projectRepository.DeleteAsync(existingProject.Id);
        }
        
        var newProject = _mapper.Map<Project>(createProjectRequest);
        newProject.SetCreatedInfo(Guid.Empty);
        
        foreach (var memberId in createProjectRequest.MemberIds)
        {
            var member = await _employeeRepository.GetAsync(e => !e.IsDeleted && e.Id == memberId);
            if (member is null)
            {
                throw new EmployeeDoesNotExistException();
            }
            
            _employeeRepository.SetModified(member);
            newProject.Employees.Add(member);
        }
        
        await _projectRepository.AddAsync(newProject).ConfigureAwait(false);
        await _unitOfWork.CommitAsync().ConfigureAwait(false);
        return new ApiActionResult(true, "Project created successfully");
    }

    public async Task<ApiActionResult> CheckIfProjectNumberExistsAsync(int projectNumber)
    {
        var projects = await _projectRepository.FindByAsync(p => !p.IsDeleted && p.ProjectNumber == projectNumber);
        var isValid = projects.IsNullOrEmpty();
        var result = new ApiActionResult(true)
        {
            Detail = isValid ? "Project number does not exist" : "Project number already exists",
            Data = isValid
        };
        return result;
    }

    public async Task<ApiActionResult> UpdateProjectAsync(UpdateProjectRequest request, Guid id, string updaterId)
    {
        var parseSuccess = Guid.TryParse(updaterId, out var updaterGuidId);
        if (!parseSuccess)
        {
            throw new InvalidGuidIdException();
        }
        
        if (!await _userRepository.ExistsAsync(u => !u.IsDeleted && u.Id == updaterGuidId))
        {
            throw new UserDoesNotExistException();
        }

        var project = await (await _projectRepository
                .FindByAsync(p => !p.IsDeleted && p.Id == id))
            .Include(p => p.Employees)
            .FirstOrDefaultAsync();

        // var project = await _projectRepository.GetAsync(p => !p.IsDeleted && p.Id == id);
        if (project is null)
        {
            throw new ProjectDoesNotExistException();
        }

        var currentVersion = project.Version;
        _mapper.Map(request, project);
        project.Employees.Clear();
        
        foreach (var memberId in request.MemberIds)
        {
            var member = await _employeeRepository.GetAsync(e => !e.IsDeleted && e.Id == memberId);
            if (member is null)
            {
                throw new EmployeeDoesNotExistException();
            }
        
            _employeeRepository.SetModified(member);
            project.Employees.Add(member);
        }
        
        if (project.Version != currentVersion)
        {
            throw new VersionMismatchedException();
        }
        
        project.SetUpdatedInfo(updaterGuidId);
        await _projectRepository.UpdateAsync(project);
        await _unitOfWork.CommitAsync();
        return new ApiActionResult(true) {Detail = "Project updated successfully"};
    }

    public async Task<ApiActionResult> DeleteProjectAsync(Guid id)
    {
        var project = await (await _projectRepository
                .FindByAsync(p => !p.IsDeleted && p.Id == id)
            ).FirstOrDefaultAsync();
        if (project is null)
        {
            throw new ProjectDoesNotExistException();
        }

        if (project.Status != ProjectStatus.NEW)
        {
            throw new IndelibleProjectException();
        }

        await _projectRepository.SoftDeleteAsync(id);
        await _unitOfWork.CommitAsync();
        return new ApiActionResult(true);
    }

    public async Task<ApiActionResult> FindProjectByProjectNumberAsync(int projectNumber)
    {
        var project = await (await _projectRepository.FindByAsync(p => !p.IsDeleted && p.ProjectNumber == projectNumber))
            .Include(p => p.Employees)
            .FirstOrDefaultAsync();
        if (project is null)
        {
            throw new ProjectDoesNotExistException();
        }

        return new ApiActionResult(true) { Data = _mapper.Map<DtoProjectDetail>(project) };
    }

    public async Task<ApiActionResult> DeleteMultipleProjectsAsync(DeleteMultipleProjectsRequest request)
    {
        foreach (var projectId in request.ProjectIds)
        {
            var project = await (await _projectRepository
                    .FindByAsync(p => !p.IsDeleted && p.Id == projectId)
                ).FirstOrDefaultAsync();
            if (project is null)
            {
                throw new ProjectDoesNotExistException();
            }

            if (project.Status != ProjectStatus.NEW)
            {
                throw new IndelibleProjectException();
            }

            await _projectRepository.DeleteAsync(projectId);
        }
        await _unitOfWork.CommitAsync();
        return new ApiActionResult(true);
    }
    public async Task<ApiActionResult> ImportProjectsFromFileAsync(IFormFile file)
    {
        var errorList = new List<string>();
        var addedProjectCount = 0;
        
        if (!file.FileName.IsExcelFile() && !file.FileName.IsCsvFile())
        {
            throw new UnsupportedFileExtensionException();
        }
        
        using var reader = file.FileName.IsExcelFile() 
            ? ExcelReaderFactory.CreateReader(file.OpenReadStream())
            : ExcelReaderFactory.CreateCsvReader(file.OpenReadStream());
        var dataTables = reader.AsDataSet().Tables;
        
        foreach (DataTable table in dataTables)
        {
            var isValidData = true;
            if (table.Rows.Count > 0 && !ExcelFileHelper.ValidHeaders(table.Rows[0].ItemArray))
            {
                throw new UnsupportedFileContentFormatException();
            }
            for (int i = 1; i < table.Rows.Count; i++)
            {
                var dataRow = table.Rows[i];

                #region Validate data row

                if (!int.TryParse(dataRow.ItemArray[0]?.ToString(), out var projectNumber))
                {
                    isValidData = false;
                    errorList.Add($"Project number is invalid at row {i}");
                }
                
                var projectName = dataRow.ItemArray[1]?.ToString() ?? string.Empty;
                var customer = dataRow.ItemArray[2]?.ToString() ?? string.Empty;
                var groupIdString = dataRow.ItemArray[3]?.ToString() ?? string.Empty;
                var memberIdString = dataRow.ItemArray[4]?.ToString() ?? string.Empty;
                var status = dataRow.ItemArray[5]?.ToString() ?? ProjectStatus.NEW;
                
                if (!DateTime.TryParse(dataRow.ItemArray[6]?.ToString(), out var startDate))
                {
                    isValidData = false;
                    errorList.Add($"Start is invalid at row {i+1}");
                }
                
                if (!DateTime.TryParse(dataRow.ItemArray[7]?.ToString(), out var endDate))
                {
                    isValidData = false;
                    errorList.Add($"End date is invalid at row {i+1}");
                }

                if (!Guid.TryParse(groupIdString, out var groupId))
                {
                    isValidData = false;
                    errorList.Add($"Group Id is invalid at row {i+1}");
                }

                var memberIds = new List<Guid>();

                foreach (var memberId in memberIdString.Split(","))
                {
                    if (memberId.IsNullOrEmpty())
                    {
                        isValidData = false;
                        errorList.Add($"Member value at row {i+1} is missing");
                        continue;
                    }
                    
                    if (!Guid.TryParse(memberId.Trim(), out var memberGuidId))
                    {
                        isValidData = false;
                        errorList.Add($"Member Id is invalid at row {i+1} ({memberId})");
                        continue;
                    }
                    
                    memberIds.Add(memberGuidId);
                }
                
                #endregion

                #region Verify data

                var project = await _projectRepository.GetAsync(p => p.ProjectNumber == projectNumber);
                if (project is not null)
                {
                    if (!project.IsDeleted)
                    {
                        isValidData = false;
                        errorList.Add($"Project number already exists at row {i+1} ({projectNumber})");
                    }
                    else
                    {
                        await _projectRepository.DeleteAsync(project.Id);
                    }
                }

                var group = await _groupRepository.GetAsync(g => g.Id == groupId && !g.IsDeleted);
                if (group is null && groupId != default)
                {
                    isValidData = false;
                    errorList.Add($"Group does not exist at row {i+1} ({groupId})");
                }
                
                foreach (var memberId in memberIds)
                {
                    if (!await _employeeRepository.ExistsAsync(e => !e.IsDeleted && e.Id == memberId))
                    {
                        isValidData = false;
                        errorList.Add($"Member with id {memberId} does not exist oa");
                    }
                }

                #endregion

                if (!isValidData)
                {
                    continue;
                }

                var projectCreate = new CreateProjectRequest()
                {
                    ProjectNumber = projectNumber,
                    Name = projectName,
                    Customer = customer,
                    GroupId = groupId,
                    MemberIds = memberIds,
                    Status = status.ToUpper(),
                    StartDate = startDate,
                    EndDate = endDate
                };
                await _projectRepository.AddAsync(_mapper.Map<Project>(projectCreate));
                addedProjectCount++;
            }
        }
        
        if (!errorList.IsNullOrEmpty())
        {
            var errorMsg = new StringBuilder();
            errorMsg.AppendJoin(", ", errorList);
            return new ApiActionResult(false) {Detail = errorMsg.ToString()};
        }

        await _unitOfWork.CommitAsync();
        return new ApiActionResult(true) {Detail = $"Added {addedProjectCount} projects"};
    }

    public async Task<FileStreamResult> ImportProjectsFromFileNpoiAsync(IFormFile file)
    {
        await using var stream = file.OpenReadStream();
        stream.Position = 0;
        var workbook = new XSSFWorkbook(stream);
        var sheet = workbook.GetSheetAt(0);

        var isValidDataFile = true;
        var isValidRow = true;
        var invalidColumns = new HashSet<ExcelColumnError>(); 
        for (var i = (sheet.FirstRowNum + 1); i <= sheet.LastRowNum; i++)
        {
            var row = sheet.GetRow(i);
            if (row is null) continue;
            if (row.Cells.All(d => d.CellType is CellType.Blank)) continue;

            #region Validate data

            var isValidProjectNumber = int.TryParse(row.GetCell(0).ToString(), out var projectNumber);
            var projectName = row.GetCell(1).ToString();
            var customer = row.GetCell(2).ToString();
            var isValidGroupId = Guid.TryParse(row.GetCell(3).ToString(), out var groupId);
            var memberIds = row.GetCell(4).ToString()?.Split(",");
            var memberGuidIds = new List<Guid>();
            var status = row.GetCell(5).ToString();
            var isValidStartDate = DateTime.TryParse(row.GetCell(6).ToString(), out var startDate);
            var isValidEndDate = DateTime.TryParse(row.GetCell(7).ToString(), out var endDate);

            if (!isValidProjectNumber)
            {
                isValidRow = false;
                invalidColumns.Add(new ExcelColumnError(0, $"Project number ({projectNumber}) is invalid"));
            }

            if (projectName is null || projectName.IsNullOrEmpty())
            {
                isValidRow = false;
                invalidColumns.Add(new ExcelColumnError(1, $"Project name ({projectName}) is invalid"));
            }

            if (customer is null || customer.IsNullOrEmpty())
            {
                isValidRow = false;
                invalidColumns.Add(new ExcelColumnError(2, $"Customer is required"));
            }

            if (!isValidGroupId)
            {
                isValidRow = false;
                invalidColumns.Add(new ExcelColumnError(3, $"Group id ({row.GetCell(3)}) is invalid"));
            }

            if (status is null || status.IsNullOrEmpty())
            {
                isValidRow = false;
                invalidColumns.Add(new ExcelColumnError(5, $"Status is required"));
            }

            if (!isValidStartDate)
            {
                isValidRow = false;
                invalidColumns.Add(new ExcelColumnError(6, $"Start date ({startDate}) is invalid"));
            }

            if (!isValidEndDate)
            {
                isValidRow = false;
                invalidColumns.Add(new ExcelColumnError(7, $"End date ({endDate}) is invalid"));
            }

            if (startDate >= endDate)
            {
                isValidRow = false;
                invalidColumns.Add(new ExcelColumnError(7, $"End date must be after start date"));
            }
            
            #endregion

            #region Verify data

            if (isValidProjectNumber &&
                await _projectRepository.ExistsAsync(p => !p.IsDeleted && p.ProjectNumber == projectNumber))
            {
                isValidRow = false;
                invalidColumns.Add(new ExcelColumnError(0, $"Project number ({projectNumber}) already exists"));
            }

            if (isValidGroupId && !await _groupRepository.ExistsAsync(g => !g.IsDeleted && g.Id == groupId))
            {
                isValidRow = false;
                invalidColumns.Add(new ExcelColumnError(3, $"Group id ({groupId}) does not exist"));
            }

            var validProjectStatuses = new[] { "NEW", "PLA", "INP", "FIN" };
            
            if (!string.IsNullOrEmpty(status) && !validProjectStatuses.Contains(status, StringComparer.OrdinalIgnoreCase))
            {
                isValidRow = false;
                invalidColumns.Add(new ExcelColumnError(5, $"Status ({status}) is not known"));
            }

            if (memberIds is not null)
            {
                foreach (var id in memberIds)
                {
                    if (id.IsNullOrEmpty()) continue;
                    if (!Guid.TryParse(id, out var memberGuid))
                    {
                        isValidRow = false;
                        invalidColumns.Add(new ExcelColumnError(4, $"Member id ({id}) is invalid"));
                        break;
                    }

                    var employee = await _employeeRepository.GetAsync(e => !e.IsDeleted && e.Id == memberGuid);
                    if (employee is null)
                    {
                        isValidRow = false;
                        invalidColumns.Add(new ExcelColumnError(4, $"Member id ({id}) does not exist"));
                        break;
                    }

                    memberGuidIds.Add(memberGuid);
                }
            }
            
            #endregion

            if (isValidRow)
            {
                var project = new CreateProjectRequest()
                {
                    ProjectNumber = projectNumber,
                    Name = projectName,
                    Customer = customer,
                    GroupId = groupId,
                    MemberIds = memberGuidIds,
                    Status = status.ToUpper(),
                    StartDate = startDate,
                    EndDate = endDate
                };

                var existingProject =
                    await _projectRepository.GetAsync(p => p.IsDeleted && p.ProjectNumber == project.ProjectNumber);
                if (existingProject is not null)
                {
                    await _projectRepository.DeleteAsync(existingProject.Id);
                }
                
                await _projectRepository.AddAsync(_mapper.Map<Project>(project));
                row.SetRowBackgroundColor(IndexedColors.Green);
                continue;
            }
            
            row.SetRowBackgroundColor(IndexedColors.LightOrange);
            foreach (var col in invalidColumns)
            {
                row.GetCell(col.ColumnNumber).CellStyle = workbook.SetBackgroundColor(IndexedColors.Red);
                row.GetCell(col.ColumnNumber).CellComment = sheet.CreateComment("PIMTool", col.ErrorDetail);
            }

            isValidDataFile = false;
            isValidRow = true;
            invalidColumns.Clear();

        }

        if (isValidDataFile)
        {
            await _unitOfWork.CommitAsync();
            return new FileStreamResult(Stream.Null, "application/excel");
        }

        await _unitOfWork.RollbackAsync();
            
        sheet.AutoSizeColumns();

        var fileName = Guid.NewGuid() + ".xlsx";
        await using var fs = File.Open(fileName, FileMode.Create);
        workbook.Write(fs);
        return new FileStreamResult(File.OpenRead(fileName), "application/excel");
    }

    public async Task<FileStreamResult> ExportProjectsToFileAsync()
    {

        #region XlsIO

        // using var engine = new ExcelEngine();
        // IApplication application = engine.Excel;
        // application.DefaultVersion = ExcelVersion.Xlsx;
        // var workbook = application.Workbooks.Create(1);
        // var worksheet = workbook.Worksheets[0];
        // worksheet.Range["A3"].Text = "46036 Michigan Ave";
        // worksheet.Range["A4"].Text = "Canton, USA";
        // worksheet.Range["A5"].Text = "Phone: +1 231-231-2310";
        //
        // MemoryStream memoryStream = new MemoryStream();
        // workbook.SaveAs(memoryStream);
        // memoryStream.Position = 0;
        // FileStreamResult result = new FileStreamResult(memoryStream, "application/excel");
        // result.FileDownloadName = "test-excel-file.xlsx";
        // return result;

        #endregion

        var projects = await (await _projectRepository.FindByAsync(p => !p.IsDeleted))
            .OrderBy(p => p.ProjectNumber)
            .Take(100)
            .Include(p => p.Group)
            .Include(p => p.Employees)
            .ToListAsync().ConfigureAwait(false);
        
       
        IWorkbook workbook = new XSSFWorkbook();
        var excelSheet = workbook.CreateSheet("Projects");
        var headerStyle = workbook.SetBackgroundColor(IndexedColors.Yellow);
        headerStyle.Alignment = HorizontalAlignment.Center;
        var fontStyle = workbook.CreateFont();
        fontStyle.IsBold = true;
        headerStyle.SetFont(fontStyle);

        var columns = new List<string>();
        var row = excelSheet.CreateRow(0);
        var columnIndex = 0;

        var dataColumns = new List<string>()
        {
            "Project number",
            "Project name",
            "Customer",
            "Group",
            "Members", 
            "Status",
            "Start date",
            "End date"
        };
               
        foreach (var column in dataColumns)
        {
            columns.Add(column);
            var cell = row.CreateCell(columnIndex++);
            cell.SetCellValue(column);
            cell.CellStyle = headerStyle;
        }

        var rowIndex = 1;
        foreach (var project in projects)
        {
            row = excelSheet.CreateRow(rowIndex);
            var cellIndex = 0;

            var cell = row.CreateCell(cellIndex++);
            cell.SetCellValue(project.ProjectNumber);
            row.CreateCell(cellIndex++).SetCellValue(project.Name);
            row.CreateCell(cellIndex++).SetCellValue(project.Customer);
            row.CreateCell(cellIndex++).SetCellValue(project.Group.Name);
            
            var employeeSb = new StringBuilder();
            employeeSb.AppendJoin(", ", project.Employees.Select(e => $"{e.FirstName} {e.LastName}"));
            
            row.CreateCell(cellIndex++).SetCellValue(employeeSb.ToString());
            row.CreateCell(cellIndex++).SetCellValue(project.Status);
            row.CreateCell(cellIndex++).SetCellValue(project.StartDate.ToShortDateString());
            var endDateString = project.EndDate.GetValueOrDefault() == default
                ? string.Empty
                : project.EndDate.GetValueOrDefault().ToShortDateString(); 
            row.CreateCell(cellIndex).SetCellValue(endDateString);
            
            rowIndex++;
        }

        columnIndex = 0;
        columns.ForEach(_ =>
        {
            excelSheet.AutoSizeColumn(columnIndex++);
        });

        var fileName = Guid.NewGuid() + ".xlsx";
        await using var fs = new FileStream(fileName, FileMode.Create, FileAccess.Write);
        workbook.Write(fs);
        return new FileStreamResult(File.OpenRead(fileName), "application/excel");
    }
}