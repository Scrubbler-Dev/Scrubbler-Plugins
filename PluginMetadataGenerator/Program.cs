using Scrubbler.PluginBase.Plugin;
using Scrubbler.PluginBase.Plugin.Account;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;

record PluginManifestEntry(
    string Id,
    string Name,
    string Version,
    string Description,
    Uri? IconUri,
    string PluginType,
    IReadOnlyList<string> SupportedPlatforms,
    Uri SourceUri
);

class Program
{
    #region Properties

    // map of plugin marker interfaces → human-friendly type labels
    private static readonly Dictionary<Type, string> _pluginTypes = new()
    {
        { typeof(IAccountPlugin), "Account Plugin" },
        { typeof(IScrobblePlugin), "Scrobble Plugin" },
        { typeof(IAutoScrobblePlugin), "Scrobble Plugin" }
        // add more here as you introduce new plugin kinds
    };

    private static readonly JsonSerializerOptions _serializerSettings = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    #endregion Properties

    static async Task Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: PluginMetadataGenerator <zipDir> <outputJson> <baseDownloadUrl>");
            Environment.Exit(1);
        }

        var zipDir = args[0];
        var outputPath = args[1];
        var baseUrl = args[2].TrimEnd('/');

        var zips = Directory.GetFiles(zipDir, "Scrubbler.Plugin.*.zip")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();
        if (zips.Length == 0)
        {
            Console.Error.WriteLine($"No plugin zips found in {zipDir}");
            throw new InvalidOperationException();
        }
        else Console.WriteLine($"Found {zips.Length} plugin zips in {zipDir}");

        var entries = new List<PluginManifestEntry>();
        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDir))
            Directory.CreateDirectory(outputDir);

        foreach (var zipFile in zips)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "scrubbler_plugin_" + Path.GetFileNameWithoutExtension(zipFile));
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
            Directory.CreateDirectory(tempDir);

            try
            {
                // extract zip to temp folder
                ZipFile.ExtractToDirectory(zipFile, tempDir);

                // find the main plugin DLL inside the zip
                var dll = Directory.GetFiles(tempDir, "Scrubbler.Plugin.*.dll", SearchOption.AllDirectories)
                                   .FirstOrDefault();

                if (dll == null)
                {
                    Console.WriteLine($"Skipping {zipFile}: no Scrubbler.Plugin.*.dll found inside");
                    continue;
                }

                Console.WriteLine($"Inspecting {Path.GetFileName(zipFile)} using {Path.GetFileName(dll)}");

                var loadContext = new PluginAssemblyLoadContext(dll);

                try
                {
                    var asm = loadContext.LoadFromAssemblyPath(dll);

                    // find all IPlugin implementations
                    var pluginTypes = GetAssemblyTypes(asm)
                        .Where(t => typeof(IPlugin).IsAssignableFrom(t) && !t.IsAbstract)
                        .OrderBy(t => t.FullName, StringComparer.Ordinal)
                        .ToArray();

                    if (pluginTypes.Length == 0)
                    {
                        Console.WriteLine($"Skipping {zipFile}: no IPlugin implementations found in {Path.GetFileName(dll)}");
                        continue;
                    }

                    foreach (var type in pluginTypes)
                    {
                        var id = type.FullName?.ToLowerInvariant() ?? Path.GetFileNameWithoutExtension(dll).ToLowerInvariant();
                        var rawVersion = asm
                            .GetCustomAttribute<AssemblyFileVersionAttribute>()?
                            .Version
                            ?? "0.0.0";

                        // trim off build metadata (e.g. +sha)
                        var version = rawVersion.Split('+')[0];

                        // resolve type label dynamically
                        var pluginTypeLabel = ResolvePluginType(type);

                        var meta = type.GetCustomAttribute<PluginMetadataAttribute>() ?? throw new InvalidOperationException($"Plugin {type.FullName} has no PluginMetadata attribute");
                        var entry = new PluginManifestEntry(
                            Id: id,
                            Name: meta.Name,
                            Version: version,
                            Description: meta.Description,
                            IconUri: new Uri($"{baseUrl}/plugins/{Path.GetFileNameWithoutExtension(zipFile) + ".png"}"),
                            PluginType: pluginTypeLabel,
                            SupportedPlatforms: meta.SupportedPlatforms.ToString().Split(", "),
                            SourceUri: new Uri($"{baseUrl}/plugins/{Path.GetFileName(zipFile)}")
                        );

                        entries.Add(entry);
                        Console.WriteLine($"Added {meta.Name} v{version} ({pluginTypeLabel})");
                    }
                }
                finally
                {
                    loadContext.Unload();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to inspect {zipFile}: {ex.Message}");
                throw;
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
            }
        }

        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(entries, _serializerSettings));
        Console.WriteLine($"Wrote {entries.Count} entries to {outputPath}");
    }

    private static string ResolvePluginType(Type pluginType)
    {
        foreach (var kvp in _pluginTypes)
        {
            if (kvp.Key.IsAssignableFrom(pluginType))
                return kvp.Value;
        }
        return "Plugin"; // fallback
    }

    private static Type[] GetAssemblyTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            foreach (var loaderException in ex.LoaderExceptions)
            {
                if (loaderException is not null)
                    Console.Error.WriteLine(loaderException.Message);
            }

            throw;
        }
    }
}

sealed class PluginAssemblyLoadContext(string pluginAssemblyPath) : AssemblyLoadContext(isCollectible: true)
{
    private static readonly Assembly SharedPluginBaseAssembly = typeof(IPlugin).Assembly;
    private static readonly string SharedPluginBaseAssemblyName = SharedPluginBaseAssembly.GetName().Name!;

    private readonly AssemblyDependencyResolver _resolver = new(pluginAssemblyPath);
    private readonly string _pluginDirectory = Path.GetDirectoryName(pluginAssemblyPath) ?? throw new InvalidOperationException("Plugin assembly path has no directory.");

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name == SharedPluginBaseAssemblyName)
            return SharedPluginBaseAssembly;

        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        if (assemblyPath is not null)
            return LoadFromAssemblyPath(assemblyPath);

        var localAssemblyPath = Path.Combine(_pluginDirectory, assemblyName.Name + ".dll");
        if (File.Exists(localAssemblyPath))
            return LoadFromAssemblyPath(localAssemblyPath);

        return null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return libraryPath is null ? 0 : LoadUnmanagedDllFromPath(libraryPath);
    }
}
