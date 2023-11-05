﻿using System.Collections.Generic;
using System.Threading.Tasks;
using PIMTool.Core.Domain.Entities;
using PIMTool.Core.Models;
using PIMTool.Core.Models.Request;

namespace PIMTool.Core.Interfaces.Services;

public interface IProjectService
{
    Task<ApiActionResult> GetAllProjectsAsync();
    Task<ApiActionResult> FindProjectsAsync(SearchProjectsRequest searchProjectsRequest);
    Task<ApiActionResult> CreateProjectAsync(CreateProjectRequest createProjectRequest);
    Task<ApiActionResult> CheckIfProjectNumberExistsAsync(int projectNumber);
}