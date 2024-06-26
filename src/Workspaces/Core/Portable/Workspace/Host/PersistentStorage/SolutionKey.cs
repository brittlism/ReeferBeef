﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.Serialization;
using Microsoft.CodeAnalysis.Host;

namespace Microsoft.CodeAnalysis.Storage;

/// <summary>
/// Handle that can be used with <see cref="IChecksummedPersistentStorage"/> to read data for a
/// <see cref="Solution"/> without needing to have the entire <see cref="Solution"/> snapshot available.
/// This is useful for cases where acquiring an entire snapshot might be expensive (for example, during 
/// solution load), but querying the data is still desired.
/// </summary>
[DataContract]
internal readonly record struct SolutionKey(
    [property: DataMember(Order = 0)] SolutionId Id,
    [property: DataMember(Order = 1)] string? FilePath)
{
    public static SolutionKey ToSolutionKey(Solution solution)
        => ToSolutionKey(solution.SolutionState);

    public static SolutionKey ToSolutionKey(SolutionState solutionState)
        => new(solutionState.Id, solutionState.FilePath);
}
