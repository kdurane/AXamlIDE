using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AXaxmlIDE.CompletionEngine.AssemblyMetadata
{
    public class DepsJsonFileAssemblyProvider : IAssemblyProvider
    {
        private readonly string _path;
        private readonly string _xamlPrimaryAssemblyPath;

        public DepsJsonFileAssemblyProvider(string executablePath, string xamlPrimaryAssemblyPath)
        {
            if (string.IsNullOrEmpty(executablePath))
                throw new ArgumentNullException(nameof(executablePath));
            _path = executablePath;
            _xamlPrimaryAssemblyPath = xamlPrimaryAssemblyPath;
        }

        private static IEnumerable<string> GetAssemblies(string path)
        {
            if (Path.GetDirectoryName(path) is not { } directory)
            {
                return [];
            }

            var depsPath = Path.Combine(directory,
                Path.GetFileNameWithoutExtension(path) + ".deps.json");
            return File.Exists(depsPath) 
                ? DepsJsonAssemblyListLoader.ParseFile(depsPath) 
                : Directory.GetFiles(directory).Where(f => f.EndsWith(".dll") || f.EndsWith(".exe"));
        }

        public IEnumerable<string> GetAssemblies()
        {
            var result = new List<string>(300);
            if (!string.IsNullOrEmpty(_xamlPrimaryAssemblyPath))
            {
                result.Add(_xamlPrimaryAssemblyPath);
            }
            try
            {
                result.AddRange(GetAssemblies(_path));
            }
            catch (Exception ex) when
                (ex is DirectoryNotFoundException or FileNotFoundException)
            {
            }
            catch (Exception ex)
            {
                throw new IOException($"Failed to read file '{_path}'.", ex);
            }
            return result;
        }
    }
}
