﻿using System;
using System.Threading.Tasks;
using StabilityMatrix.Core.Models.Api.Lykos;

namespace StabilityMatrix.Avalonia.Services;

public interface IAccountsService
{
    event EventHandler<LykosAccountStatusUpdateEventArgs>? LykosAccountStatusUpdate;

    LykosAccountStatusUpdateEventArgs? LykosStatus { get; }

    Task LykosSignupAsync(string email, string password, string username);

    Task LykosLoginAsync(string email, string password);

    Task LykosLogoutAsync();

    Task RefreshAsync();
}
