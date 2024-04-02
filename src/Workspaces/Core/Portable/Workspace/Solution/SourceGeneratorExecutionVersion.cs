﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Serialization;
using Microsoft.CodeAnalysis.Collections;
using Microsoft.CodeAnalysis.Host;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis;

/// <summary>
/// Represents the version of source generator execution that a project is at. Source generator results are kept around
/// as long as this version stays the same and we are in <see cref="SourceGeneratorExecutionPreference.Balanced"/>
/// mode. This has no effect when in <see cref="SourceGeneratorExecutionPreference.Automatic"/> mode (as we always rerun
/// generators on any change). This should effectively be used as a monotonically increasing value.
/// </summary>
/// <param name="MajorVersion">Controls the major version of source generation execution.  When this changes the
/// generator driver should be dropped and all generation should be rerun.</param>
/// <param name="MinorVersion">Controls the minor version of source generation execution.  When this changes the
/// generator driver can be reused and should incrementally determine what the new generated documents should be.
/// </param>
[DataContract]
internal readonly record struct SourceGeneratorExecutionVersion(
    [property: DataMember(Order = 0)] int MajorVersion,
    [property: DataMember(Order = 1)] int MinorVersion)
{
    public SourceGeneratorExecutionVersion IncrementMajorVersion()
        => new(MajorVersion + 1, MinorVersion: 0);

    public SourceGeneratorExecutionVersion IncrementMinorVersion()
        => new(MajorVersion, MinorVersion + 1);

    public void WriteTo(ObjectWriter writer)
    {
        writer.WriteInt32(MajorVersion);
        writer.WriteInt32(MinorVersion);
    }

    public static SourceGeneratorExecutionVersion ReadFrom(ObjectReader reader)
        => new(reader.ReadInt32(), reader.ReadInt32());
}

internal readonly struct SourceGeneratorExecutionVersionMap
{
    public static readonly SourceGeneratorExecutionVersionMap Empty = new(default);

    private readonly ImmutableSegmentedDictionary<ProjectId, SourceGeneratorExecutionVersion> _map;

    public SourceGeneratorExecutionVersionMap(ImmutableSegmentedDictionary<ProjectId, SourceGeneratorExecutionVersion> map)
    {
        _map = map.IsDefault ? ImmutableSegmentedDictionary<ProjectId, SourceGeneratorExecutionVersion>.Empty : map;
    }

    public SourceGeneratorExecutionVersion this[ProjectId projectId] => _map[projectId];

    public static bool operator ==(SourceGeneratorExecutionVersionMap map1, SourceGeneratorExecutionVersionMap map2)
        => map1._map == map2._map;

    public static bool operator !=(SourceGeneratorExecutionVersionMap map1, SourceGeneratorExecutionVersionMap map2)
        => !(map1 == map2);

    public override int GetHashCode()
        => throw new InvalidOperationException();

    public override bool Equals([NotNullWhen(true)] object? obj)
        => obj is SourceGeneratorExecutionVersionMap map && this == map;

    public readonly Builder ToBuilder()
        => new(_map.ToBuilder());

    public void WriteTo(ObjectWriter writer)
    {
        writer.WriteInt32(_map.Count);
        foreach (var (projectId, version) in _map)
        {
            projectId.WriteTo(writer);
            version.WriteTo(writer);
        }
    }

    public static SourceGeneratorExecutionVersionMap Deserialize(ObjectReader reader)
    {
        var count = reader.ReadInt32();
        var builder = ImmutableSegmentedDictionary.CreateBuilder<ProjectId, SourceGeneratorExecutionVersion>();
        for (var i = 0; i < count; i++)
        {
            var projectId = ProjectId.ReadFrom(reader);
            var version = SourceGeneratorExecutionVersion.ReadFrom(reader);
            builder.Add(projectId, version);
        }

        return new(builder.ToImmutable());
    }

    public struct Builder
    {
        private readonly ImmutableSegmentedDictionary<ProjectId, SourceGeneratorExecutionVersion>.Builder _builder;

        public Builder(ImmutableSegmentedDictionary<ProjectId, SourceGeneratorExecutionVersion>.Builder builder)
        {
            _builder = builder;
        }

        public readonly SourceGeneratorExecutionVersion this[ProjectId projectId]
        {
            get => _builder[projectId];
            set => _builder[projectId] = value;
        }

        public readonly void Add(ProjectId projectId, SourceGeneratorExecutionVersion version)
            => _builder.Add(projectId, version);

        public readonly void Remove(ProjectId projectId)
            => _builder.Remove(projectId);

        public readonly SourceGeneratorExecutionVersionMap ToImmutable()
            => new(_builder.ToImmutable());
    }
}
