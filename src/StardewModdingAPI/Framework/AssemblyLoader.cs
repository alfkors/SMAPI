﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using StardewModdingAPI.AssemblyRewriters;

namespace StardewModdingAPI.Framework
{
    /// <summary>Preprocesses and loads mod assemblies.</summary>
    internal class AssemblyLoader
    {
        /*********
        ** Properties
        *********/
        /// <summary>Metadata for mapping assemblies to the current platform.</summary>
        private readonly PlatformAssemblyMap AssemblyMap;

        /// <summary>A type => assembly lookup for types which should be rewritten.</summary>
        private readonly IDictionary<string, Assembly> TypeAssemblies;

        /// <summary>Encapsulates monitoring and logging.</summary>
        private readonly IMonitor Monitor;


        /*********
        ** Public methods
        *********/
        /// <summary>Construct an instance.</summary>
        /// <param name="targetPlatform">The current game platform.</param>
        /// <param name="monitor">Encapsulates monitoring and logging.</param>
        public AssemblyLoader(Platform targetPlatform, IMonitor monitor)
        {
            this.Monitor = monitor;
            this.AssemblyMap = Constants.GetAssemblyMap(targetPlatform);

            // generate type => assembly lookup for types which should be rewritten
            this.TypeAssemblies = new Dictionary<string, Assembly>();
            foreach (Assembly assembly in this.AssemblyMap.Targets)
            {
                ModuleDefinition module = this.AssemblyMap.TargetModules[assembly];
                foreach (TypeDefinition type in module.GetTypes())
                {
                    if (!type.IsPublic)
                        continue; // no need to rewrite
                    if (type.Namespace.Contains("<"))
                        continue; // ignore assembly metadata
                    this.TypeAssemblies[type.FullName] = assembly;
                }
            }
        }

        /// <summary>Preprocess and load an assembly.</summary>
        /// <param name="assemblyPath">The assembly file path.</param>
        /// <param name="assumeCompatible">Assume the mod is compatible, even if incompatible code is detected.</param>
        /// <returns>Returns the rewrite metadata for the preprocessed assembly.</returns>
        /// <exception cref="IncompatibleInstructionException">An incompatible CIL instruction was found while rewriting the assembly.</exception>
        public Assembly Load(string assemblyPath, bool assumeCompatible)
        {
            // get referenced local assemblies
            AssemblyParseResult[] assemblies;
            {
                AssemblyDefinitionResolver resolver = new AssemblyDefinitionResolver();
                HashSet<string> visitedAssemblyNames = new HashSet<string>(AppDomain.CurrentDomain.GetAssemblies().Select(p => p.GetName().Name)); // don't try loading assemblies that are already loaded
                assemblies = this.GetReferencedLocalAssemblies(new FileInfo(assemblyPath), visitedAssemblyNames, resolver).ToArray();
                if (!assemblies.Any())
                    throw new InvalidOperationException($"Could not load '{assemblyPath}' because it doesn't exist.");
                resolver.Add(assemblies.Select(p => p.Definition).ToArray());
            }

            // rewrite & load assemblies in leaf-to-root order
            Assembly lastAssembly = null;
            foreach (AssemblyParseResult assembly in assemblies)
            {
                bool changed = this.RewriteAssembly(assembly.Definition, assumeCompatible);
                if (changed)
                {
                    this.Monitor.Log($"Loading {assembly.File.Name} (rewritten in memory)...", LogLevel.Trace);
                    using (MemoryStream outStream = new MemoryStream())
                    {
                        assembly.Definition.Write(outStream);
                        byte[] bytes = outStream.ToArray();
                        lastAssembly = Assembly.Load(bytes);
                    }
                }
                else
                {
                    this.Monitor.Log($"Loading {assembly.File.Name}...", LogLevel.Trace);
                    lastAssembly = Assembly.UnsafeLoadFrom(assembly.File.FullName);
                }
            }

            // last assembly loaded is the root
            return lastAssembly;
        }

        /// <summary>Resolve an assembly by its name.</summary>
        /// <param name="name">The assembly name.</param>
        /// <remarks>
        /// This implementation returns the first loaded assembly which matches the short form of
        /// the assembly name, to resolve assembly resolution issues when rewriting
        /// assemblies (especially with Mono). Since this is meant to be called on <see cref="AppDomain.AssemblyResolve"/>,
        /// the implicit assumption is that loading the exact assembly failed.
        /// </remarks>
        public Assembly ResolveAssembly(string name)
        {
            string shortName = name.Split(new[] { ',' }, 2).First(); // get simple name (without version and culture)
            return AppDomain.CurrentDomain
                .GetAssemblies()
                .FirstOrDefault(p => p.GetName().Name == shortName);
        }


        /*********
        ** Private methods
        *********/
        /****
        ** Assembly parsing
        ****/
        /// <summary>Get a list of referenced local assemblies starting from the mod assembly, ordered from leaf to root.</summary>
        /// <param name="file">The assembly file to load.</param>
        /// <param name="visitedAssemblyNames">The assembly names that should be skipped.</param>
        /// <param name="assemblyResolver">A resolver which resolves references to known assemblies.</param>
        /// <returns>Returns the rewrite metadata for the preprocessed assembly.</returns>
        private IEnumerable<AssemblyParseResult> GetReferencedLocalAssemblies(FileInfo file, HashSet<string> visitedAssemblyNames, IAssemblyResolver assemblyResolver)
        {
            // validate
            if (file.Directory == null)
                throw new InvalidOperationException($"Could not get directory from file path '{file.FullName}'.");
            if (!file.Exists)
                yield break; // not a local assembly

            // read assembly
            byte[] assemblyBytes = File.ReadAllBytes(file.FullName);
            AssemblyDefinition assembly;
            using (Stream readStream = new MemoryStream(assemblyBytes))
                assembly = AssemblyDefinition.ReadAssembly(readStream, new ReaderParameters(ReadingMode.Deferred) { AssemblyResolver = assemblyResolver });

            // skip if already visited
            if (visitedAssemblyNames.Contains(assembly.Name.Name))
                yield break;
            visitedAssemblyNames.Add(assembly.Name.Name);

            // yield referenced assemblies
            foreach (AssemblyNameReference dependency in assembly.MainModule.AssemblyReferences)
            {
                FileInfo dependencyFile = new FileInfo(Path.Combine(file.Directory.FullName, $"{dependency.Name}.dll"));
                foreach (AssemblyParseResult result in this.GetReferencedLocalAssemblies(dependencyFile, visitedAssemblyNames, assemblyResolver))
                    yield return result;
            }

            // yield assembly
            yield return new AssemblyParseResult(file, assembly);
        }

        /****
        ** Assembly rewriting
        ****/
        /// <summary>Rewrite the types referenced by an assembly.</summary>
        /// <param name="assembly">The assembly to rewrite.</param>
        /// <param name="assumeCompatible">Assume the mod is compatible, even if incompatible code is detected.</param>
        /// <returns>Returns whether the assembly was modified.</returns>
        /// <exception cref="IncompatibleInstructionException">An incompatible CIL instruction was found while rewriting the assembly.</exception>
        private bool RewriteAssembly(AssemblyDefinition assembly, bool assumeCompatible)
        {
            ModuleDefinition module = assembly.MainModule;
            HashSet<string> loggedMessages = new HashSet<string>();

            // swap assembly references if needed (e.g. XNA => MonoGame)
            bool platformChanged = false;
            for (int i = 0; i < module.AssemblyReferences.Count; i++)
            {
                // remove old assembly reference
                if (this.AssemblyMap.RemoveNames.Any(name => module.AssemblyReferences[i].Name == name))
                {
                    this.LogOnce(this.Monitor, loggedMessages, $"Rewriting {assembly.Name.Name} for OS...");
                    platformChanged = true;
                    module.AssemblyReferences.RemoveAt(i);
                    i--;
                }
            }
            if (platformChanged)
            {
                // add target assembly references
                foreach (AssemblyNameReference target in this.AssemblyMap.TargetReferences.Values)
                    module.AssemblyReferences.Add(target);

                // rewrite type scopes to use target assemblies
                IEnumerable<TypeReference> typeReferences = module.GetTypeReferences().OrderBy(p => p.FullName);
                foreach (TypeReference type in typeReferences)
                    this.ChangeTypeScope(type);
            }

            // find incompatible instructions
            bool anyRewritten = false;
            IInstructionFinder[] finders = Constants.GetIncompatibilityFinders().ToArray();
            IInstructionRewriter[] rewriters = Constants.GetRewriters().ToArray();
            foreach (MethodDefinition method in this.GetMethods(module))
            {
                ILProcessor cil = method.Body.GetILProcessor();
                foreach (Instruction instruction in cil.Body.Instructions.ToArray())
                {
                    // throw exception if instruction is incompatible but can't be rewritten
                    IInstructionFinder finder = finders.FirstOrDefault(p => p.IsMatch(instruction, platformChanged));
                    if (finder != null)
                    {
                        if (!assumeCompatible)
                            throw new IncompatibleInstructionException(finder.NounPhrase, $"Found an incompatible CIL instruction ({finder.NounPhrase}) while loading assembly {assembly.Name.Name}.");
                        this.LogOnce(this.Monitor, loggedMessages, $"Found an incompatible CIL instruction ({finder.NounPhrase}) while loading assembly {assembly.Name.Name}, but SMAPI is configured to allow it anyway. The mod may crash or behave unexpectedly.", LogLevel.Warn);
                    }

                    // rewrite instruction if needed
                    IInstructionRewriter rewriter = rewriters.FirstOrDefault(p => p.IsMatch(instruction, platformChanged));
                    if (rewriter != null)
                    {
                        this.LogOnce(this.Monitor, loggedMessages, $"Rewriting {assembly.Name.Name} to fix {rewriter.NounPhrase}...");
                        rewriter.Rewrite(module, cil, instruction, this.AssemblyMap);
                        anyRewritten = true;
                    }
                }
            }

            return platformChanged || anyRewritten;
        }

        /// <summary>Get the correct reference to use for compatibility with the current platform.</summary>
        /// <param name="type">The type reference to rewrite.</param>
        private void ChangeTypeScope(TypeReference type)
        {
            // check skip conditions
            if (type == null || type.FullName.StartsWith("System."))
                return;

            // get assembly
            Assembly assembly;
            if (!this.TypeAssemblies.TryGetValue(type.FullName, out assembly))
                return;

            // replace scope
            AssemblyNameReference assemblyRef = this.AssemblyMap.TargetReferences[assembly];
            type.Scope = assemblyRef;
        }

        /// <summary>Get all methods in a module.</summary>
        /// <param name="module">The module to search.</param>
        private IEnumerable<MethodDefinition> GetMethods(ModuleDefinition module)
        {
            return (
                from type in module.GetTypes()
                where type.HasMethods
                from method in type.Methods
                where method.HasBody
                select method
            );
        }

        /// <summary>Log a message for the player or developer the first time it occurs.</summary>
        /// <param name="monitor">The monitor through which to log the message.</param>
        /// <param name="hash">The hash of logged messages.</param>
        /// <param name="message">The message to log.</param>
        /// <param name="level">The log severity level.</param>
        private void LogOnce(IMonitor monitor, HashSet<string> hash, string message, LogLevel level = LogLevel.Trace)
        {
            if (!hash.Contains(message))
            {
                this.Monitor.Log(message, level);
                hash.Add(message);
            }
        }
    }
}
