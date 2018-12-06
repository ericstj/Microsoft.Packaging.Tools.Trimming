﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Build.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Microsoft.DotNet.Build.Tasks
{
    internal class FileNode : IIsIncluded
    {
        private ISet<FileNode> dependencies;
        private ISet<FileNode> relatedFiles = new HashSet<FileNode>();
        private bool followTypeForwards;
        private IDictionary<string, FileNode> typeForwards = new Dictionary<string, FileNode>();

        internal const string NuGetPackageIdMetadata = "NuGetPackageId";
        internal const string NuGetPackageVersionMetadata = "NuGetPackageVersion";
        internal const string AdditionalDependenciesFileSuffix = ".dependencies";

        private FileNode(string fileName)
        {
            Name = fileName;
            dependencies = new HashSet<FileNode>();
        }

        public FileNode(ITaskItem fileItem, IDictionary<string, NuGetPackageNode> allPackages)
        {
            Name = fileItem.GetMetadata("Filename") + fileItem.GetMetadata("Extension");
            OriginalItem = fileItem;
            PackageId = fileItem.GetMetadata(NuGetPackageIdMetadata);
            SourceFile = fileItem.GetMetadata("FullPath");

            if (string.IsNullOrEmpty(PackageId))
            {
                PackageId = NuGetUtilities.GetPackageIdFromSourcePath(SourceFile);
            }

            if (!string.IsNullOrEmpty(PackageId))
            {
                NuGetPackageNode package;

                if (!allPackages.TryGetValue(PackageId, out package))
                {
                    // some file came from a package that wasn't found in the lock file
                }
                else
                {
                    Package = package;
                    Package.Files.Add(this);
                }
            }
        }

        public static FileNode CreateAggregateFileNode(FileNode fileNode)
        {
            var aggregateNode = new FileNode(fileNode.Name);
            aggregateNode.AddCandidateImplementation(fileNode);
            return aggregateNode;
        }

        public bool IsAggregate { get { return OriginalItem == null; } }
        public bool IsIncluded { get; set; }
        public bool IsMissing { get; set; }
        public string Name { get; }
        public ITaskItem OriginalItem { get; }
        public string PackageId { get; }
        public string SourceFile { get; }
        public NuGetPackageNode Package { get; }
        public IEnumerable<FileNode> Dependencies { get { return dependencies ?? Enumerable.Empty<FileNode>(); } }
        public IEnumerable<FileNode> RelatedFiles { get { return relatedFiles; } }

        public override string ToString()
        {
            return Name;
        }

        public void PopulateDependencies(IDictionary<string, FileNode> allFiles, bool preferNativeImage, ILog log)
        {
            if (IsAggregate)
            {
                // dependencies of aggregate nodes will not appear in allFiles, so pass through
                // dependency population
                foreach(var dependency in dependencies)
                {
                    dependency.PopulateDependencies(allFiles, preferNativeImage, log);
                }
            }
            else  if (dependencies == null)
            {
                PopulateDependenciesInternal(allFiles, preferNativeImage, log, null);
            }
        }

        public void AddCandidateImplementation(FileNode candidate)
        {
            if (IsAggregate != true)
            {
                throw new InvalidOperationException($"{nameof(AddCandidateImplementation)} can only be called on aggregate FileNodes.");
            }

            if (candidate.Name != Name)
            {
                throw new ArgumentException($"{candidate.Name} does not match this aggregate {nameof(FileNode)}'s {nameof(Name)}: {Name}.", nameof(candidate));
            }

            dependencies.Add(candidate);
        }

        private void PopulateDependenciesInternal(IDictionary<string, FileNode> allFiles, bool preferNativeImage, ILog log, Stack<FileNode> stack)
        {
            if (stack == null)
            {
                stack = new Stack<FileNode>();
            }

            if (stack.Contains(this))
            {
                log.LogMessage($"Cycle detected: {String.Join(" -> ", stack)} -> {this}");
            }

            if (dependencies != null)
            {
                return;
            }

            stack.Push(this);

            dependencies = new HashSet<FileNode>();
            
            if (File.Exists(SourceFile))
            {
                PopulateDependenciesFromMetadata(allFiles, preferNativeImage, log, stack);
            }
            else
            {
                IsMissing = true;
            }

            // allow for components to specify their dependencies themselves, by placing a file next to their source file.
            var additionalDependenciesFile = SourceFile + AdditionalDependenciesFileSuffix;

            if (File.Exists(additionalDependenciesFile))
            {
                foreach (var additionalDependency in File.ReadAllLines(additionalDependenciesFile))
                {
                    if (additionalDependency.Length == 0 || additionalDependency[0] == '#')
                    {
                        continue;
                    }

                    FileNode additionalDependencyFile;
                    if (allFiles.TryGetValue(additionalDependency, out additionalDependencyFile))
                    {
                        dependencies.Add(additionalDependencyFile);
                    }
                    else
                    {
                        log.LogMessage(LogImportance.Low, $"Could not locate explicit dependency {additionalDependency} of {SourceFile} specified in {additionalDependenciesFile}.");
                    }
                }
            }

            // Files may be related to other files via OriginalItemSpec
            if (OriginalItem != null)
            {
                var relatedToPath = OriginalItem.GetMetadata("OriginalItemSpec");

                if (!String.IsNullOrEmpty(relatedToPath))
                {
                    var relatedToFileName = Path.GetFileName(relatedToPath);

                    FileNode relatedTo = null;
                    if (allFiles.TryGetValue(relatedToFileName, out relatedTo))
                    {
                        if (relatedTo.IsAggregate)
                        {
                            bool found = false;
                            foreach (var dependency in relatedTo.Dependencies)
                            {
                                if (dependency.SourceFile.Equals(relatedToPath, StringComparison.OrdinalIgnoreCase))
                                {
                                    found = true;
                                    dependency.relatedFiles.Add(this);
                                    break;
                                }
                            }

                            if (!found)
                            {
                                log.LogMessage(LogImportance.Low, $"Could not locate explicit parent {relatedToPath} of {SourceFile} specified in OriginalItemSpec.  Considered {string.Join(";", relatedTo.Dependencies.Select(d => d.SourceFile))} but they did not match");
                            }
                        }
                        else if (relatedTo.SourceFile.Equals(relatedToPath, StringComparison.OrdinalIgnoreCase))
                        {
                            relatedTo.relatedFiles.Add(this);
                        }
                        else
                        {
                            log.LogMessage(LogImportance.Low, $"Could not locate explicit parent {relatedToPath} of {SourceFile} specified in OriginalItemSpec.  Considered {relatedTo.SourceFile} but it didn't match.");
                        }
                    }
                    
                    if (relatedTo == null)
                    {
                        log.LogMessage(LogImportance.Low, $"Could not locate explicit parent {relatedToPath} of {SourceFile} specified in OriginalItemSpec.");
                    }
                }
            }

            stack.Pop();
        }

        private void PopulateDependenciesFromMetadata(IDictionary<string, FileNode> allFiles, bool preferNativeImage, ILog log, Stack<FileNode> stack)
        {
            try
            { 
                using (var peReader = new PEReader(new FileStream(SourceFile, FileMode.Open, FileAccess.Read, FileShare.Delete | FileShare.Read)))
                {
                    if (peReader.HasMetadata)
                    {
                        var reader = peReader.GetMetadataReader();

                        var includeDependencies = true;

                        // map of facade handles to enable quickly getting to FileNode without repeatedly looking up by name
                        var facadeHandles = new Dictionary<AssemblyReferenceHandle, FileNode>();

                        if (IsFullFacade(reader))
                        {
                            // don't include dependencies in full facades.  We'll instead follow their typeforwards and promote the dependencies to the parent.
                            includeDependencies = false;

                            // follow typeforwards in any full facade.
                            followTypeForwards = true;
                        }

                        foreach (var handle in reader.AssemblyReferences)
                        {
                            var reference = reader.GetAssemblyReference(handle);
                            var referenceName = reader.GetString(reference.Name);

                            FileNode referencedFile = TryGetFileForReference(referenceName, allFiles, preferNativeImage);

                            if (referencedFile != null)
                            {
                                if (includeDependencies)
                                {
                                    dependencies.Add(referencedFile);
                                }

                                // populate dependencies of child
                                referencedFile.PopulateDependenciesInternal(allFiles, preferNativeImage, log, stack);

                                // if we're following type-forwards out of any dependency make sure to look at typerefs from this assembly.
                                // and populate the type-forwards in the dependency
                                if (referencedFile.followTypeForwards || followTypeForwards)
                                {
                                    facadeHandles.Add(handle, referencedFile);
                                }
                            }
                            else
                            {
                                // static dependency that wasn't satisfied, this can happen if folks use 
                                // lightup code to guard the static dependency.
                                // this can also happen when referencing a package that isn't implemented
                                // on this platform but don't fail the build here
                                log.LogMessage(LogImportance.Low, $"Could not locate assembly dependency {referenceName} of {SourceFile}.");
                            }
                        }

                        if (followTypeForwards)
                        {
                            // if following typeforwards out of this assembly, capture all type forwards
                            foreach (var exportedTypeHandle in reader.ExportedTypes)
                            {
                                var exportedType = reader.GetExportedType(exportedTypeHandle);

                                if (exportedType.IsForwarder)
                                {
                                    var assemblyReferenceHandle = (AssemblyReferenceHandle)exportedType.Implementation;
                                    FileNode assemblyReferenceNode;

                                    if (facadeHandles.TryGetValue(assemblyReferenceHandle, out assemblyReferenceNode))
                                    {
                                        var typeName = exportedType.Namespace.IsNil ?
                                            reader.GetString(exportedType.Name) :
                                            reader.GetString(exportedType.Namespace) + reader.GetString(exportedType.Name);

                                        typeForwards.Add(typeName, assemblyReferenceNode);
                                    }
                                }
                            }
                        }
                        else if (facadeHandles.Count > 0)
                        {
                            // if examining type forwards in some dependency, enumerate type-refs
                            // for any that point at a facade assembly.

                            foreach (var typeReferenceHandle in reader.TypeReferences)
                            {
                                var typeReference = reader.GetTypeReference(typeReferenceHandle);
                                var resolutionScope = typeReference.ResolutionScope;

                                if (resolutionScope.Kind == HandleKind.AssemblyReference)
                                {
                                    var assemblyReferenceHandle = (AssemblyReferenceHandle)resolutionScope;
                                    FileNode assemblyReferenceNode;

                                    if (facadeHandles.TryGetValue(assemblyReferenceHandle, out assemblyReferenceNode))
                                    {
                                        var typeName = typeReference.Namespace.IsNil ?
                                            reader.GetString(typeReference.Name) :
                                            reader.GetString(typeReference.Namespace) + reader.GetString(typeReference.Name);

                                        FileNode typeForwardedToNode = null;

                                        var forwardAssemblies = new Stack<FileNode>();

                                        // while assembly forwarded to is also a facade, add a dependency on the target
                                        while (assemblyReferenceNode.followTypeForwards)
                                        {
                                            if (!assemblyReferenceNode.typeForwards.TryGetValue(typeName, out typeForwardedToNode))
                                            {
                                                break;
                                            }

                                            dependencies.Add(typeForwardedToNode);
                                            forwardAssemblies.Push(assemblyReferenceNode);

                                            // look at the target in case it is also a facade
                                            assemblyReferenceNode = typeForwardedToNode;

                                            if (forwardAssemblies.Contains(assemblyReferenceNode))
                                            {
                                                // type-forward cycle, bail
                                                log.LogMessage($"Cycle detected involving type-forwards: {String.Join(" -> ", forwardAssemblies)}");
                                                break;
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        // examine native module dependencies
                        for (int i = 1, count = reader.GetTableRowCount(TableIndex.ModuleRef); i <= count; i++)
                        {
                            var moduleRef = reader.GetModuleReference(MetadataTokens.ModuleReferenceHandle(i));
                            var moduleName = reader.GetString(moduleRef.Name);

                            var moduleRefCandidates = new[] { moduleName, moduleName + ".dll", moduleName + ".so", moduleName + ".dylib" };

                            FileNode referencedNativeFile = null;
                            foreach (var moduleRefCandidate in moduleRefCandidates)
                            {
                                if (allFiles.TryGetValue(moduleRefCandidate, out referencedNativeFile))
                                {
                                    break;
                                }
                            }

                            if (referencedNativeFile != null)
                            {
                                dependencies.Add(referencedNativeFile);
                            }
                            else
                            {
                                // DLLImport that wasn't satisfied
                            }
                        }
                    }
                }
            }
            catch(BadImageFormatException)
            {
                // not a PE
            }
        }

        private FileNode TryGetFileForReference(string referenceName, IDictionary<string, FileNode> allFiles, bool preferNativeImage)
        {
            var referenceCandidates = preferNativeImage ?
                new[] { referenceName + ".ni.dll", referenceName + ".dll" } :
                new[] { referenceName + ".dll", referenceName + ".ni.dll" };

            FileNode referencedFile = null;
            foreach (var referenceCandidate in referenceCandidates)
            {
                if (allFiles.TryGetValue(referenceCandidate, out referencedFile))
                {
                    break;
                }
            }

            return referencedFile;
        }

        private static bool IsFullFacade(MetadataReader reader)
        {
            foreach (var typeDefHandle in reader.TypeDefinitions)
            {
                var typeDef = reader.GetTypeDefinition(typeDefHandle);
                var typeName = reader.GetString(typeDef.Name);

                if (typeName != "<Module>")
                {
                    return false;
                }
            }

            return true;
        }
    }
}
