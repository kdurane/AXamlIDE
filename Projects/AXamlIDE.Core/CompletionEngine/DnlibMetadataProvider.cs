using System.Collections.Generic;
using System.IO;
using System.Linq;
using AXamlIDE.CompletionEngine.AssemblyMetadata;
using dnlib.DotNet;

namespace AXamlIDE.CompletionEngine;

    public class DnlibMetadataProvider : IMetadataProvider
    {
        public IMetadataReaderSession GetMetadata(IEnumerable<string> paths)
        {
            return new DnlibMetadataProviderSession([.. paths]);
        }
    }

    internal class DnlibMetadataProviderSession : IMetadataReaderSession
    {
        private readonly ModuleContext _modCtx;
        private readonly Dictionary<ITypeDefOrRef, ITypeDefOrRef> _baseTypes = [];
        private readonly Dictionary<ITypeDefOrRef, TypeDef> _baseTypeDefs = [];
        private readonly Dictionary<TypeDef, (List<TypeSig> Sigs, List<string> Names)> _hierarchies = [];
        public string? TargetAssemblyName { get; private set; }
        public IReadOnlyCollection<IAssemblyInformation> Assemblies { get; }
        public DnlibMetadataProviderSession(string[] directoryPath)
        {
            var asmResolver = new AssemblyResolver()
            {
                EnableTypeDefCache = false,
                UseGAC = false
            };
            var resolver = new Resolver(asmResolver)
            {
                ProjectWinMDRefs = false
            };
            _modCtx = new ModuleContext(asmResolver, resolver);
            asmResolver.DefaultModuleContext = _modCtx;

            if (directoryPath == null || directoryPath.Length == 0)
            {
                TargetAssemblyName = null;
                Assemblies = [];
            }
            else
            {
                TargetAssemblyName = System.Reflection.AssemblyName.GetAssemblyName(directoryPath[0]).ToString();
                Assemblies = LoadAssemblies(_modCtx, directoryPath).Select(a => new AssemblyWrapper(a, this)).ToList();
            }
        }

        public bool IsTargetAssembly(IAssembly? assembly)
            => assembly is AssemblyDef def && def.GetFullNameWithPublicKeyToken() == TargetAssemblyName;

        public TypeDef? GetTypeDef(ITypeDefOrRef type)
        {
            switch (type)
            {
                case null:
                    return null;
                case TypeDef typeDef:
                    return typeDef;
            }

            if (_baseTypeDefs.TryGetValue(type, out var baseType))
            {
                return baseType;
            }
            else
            {
                return _baseTypeDefs[type] = type.ResolveTypeDef();
            }
        }

        public ITypeDefOrRef GetBaseType(ITypeDefOrRef type)
        {
            if (_baseTypes.TryGetValue(type, out var baseType))
            {
                return baseType;
            }
            else
            {
                return _baseTypes[type] = type.GetBaseType();
            }

        }

        /// <summary>
        /// The type, then each base class, each followed by the interfaces it declares, with generic parameters
        /// replaced by the arguments given further down (Controls : AvaloniaList&lt;Control&gt; gives
        /// ICollection&lt;Control&gt;, not ICollection&lt;T&gt;), and their full names.
        /// Cached per type: every control repeats its base classes' list.
        /// </summary>
        public (List<TypeSig> Sigs, List<string> Names) GetHierarchy(TypeDef type)
        {
            if (_hierarchies.TryGetValue(type, out var cached))
                return cached;
            _hierarchies[type] = ([], []); // stops a loop in broken metadata
            return _hierarchies[type] = WalkHierarchy(type, type.ToTypeSig(), null, depth: 0);
        }

        private (List<TypeSig> Sigs, List<string> Names) WalkHierarchy(TypeDef def, TypeSig sig, IList<TypeSig>? arguments, int depth)
        {
            List<TypeSig> sigs = [sig];
            foreach (var implemented in def.Interfaces)
                sigs.Add(Substitute(implemented.Interface.ToTypeSig(), arguments));
            var names = sigs.Select(s => s.FullName).ToList();

            if (depth < 32 && def.BaseType is { } baseType)
            {
                var baseSig = Substitute(baseType.ToTypeSig(), arguments);
                if (GetTypeDef(baseType) is not { } baseDef)
                {
                    sigs.Add(baseSig);
                    names.Add(baseSig.FullName);
                }
                else
                {
                    // Only a generic base (AvaloniaList<Control>) depends on the arguments; the rest are shared
                    var (baseSigs, baseNames) = baseSig is GenericInstSig generic
                        ? WalkHierarchy(baseDef, baseSig, generic.GenericArguments, depth + 1)
                        : GetHierarchy(baseDef);
                    sigs.AddRange(baseSigs);
                    names.AddRange(baseNames);
                }
            }
            return (sigs, names);
        }

        private static TypeSig Substitute(TypeSig sig, IList<TypeSig>? arguments)
        {
            if (arguments is null)
                return sig;
            return sig switch
            {
                GenericVar variable when variable.Number < arguments.Count => arguments[(int)variable.Number],
                GenericInstSig generic => new GenericInstSig(generic.GenericType,
                    generic.GenericArguments.Select(argument => Substitute(argument, arguments)).ToList()),
                _ => sig,
            };
        }

        private static List<AssemblyDef> LoadAssemblies(ModuleContext context, string[] lst)
        {
            var asmResovler = (AssemblyResolver)context.AssemblyResolver;

            foreach (var path in lst)
                asmResovler.PreSearchPaths.Add(path);

            List<AssemblyDef> assemblies = [];

            foreach (var asm in lst)
            {
                try
                {
                    var creationOptions = new ModuleCreationOptions(context)
                    {
                        TryToLoadPdbFromDisk = false
                    };
                    var def = AssemblyDef.Load(File.ReadAllBytes(asm), creationOptions);
                    asmResovler.AddToCache(def);
                    assemblies.Add(def);
                }
                catch
                {
                    //Ignore
                }
            }

            return assemblies;
        }

        public void Dispose()
        {
            _baseTypes.Clear();
            _baseTypeDefs.Clear();
            _hierarchies.Clear();
            ((AssemblyResolver)_modCtx.AssemblyResolver).Clear();
        }
    }
