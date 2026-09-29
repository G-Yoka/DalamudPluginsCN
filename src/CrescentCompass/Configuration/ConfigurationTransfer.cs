using System.Reflection;
using System.Text.Json;
using CrescentCompass.Core;

namespace CrescentCompass.Configuration;

public static class ConfigurationTransfer
{
    private const string FormatName = "CrescentCompass.Configuration";
    private const string RouteFormatName = "CrescentCompass.NavigationRoute";
    private const int FormatVersion = 1;
    private const long MaximumImportBytes = 64L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    public static void Export(PluginConfiguration configuration, string path)
    {
        var package = new ConfigurationExportPackage
        {
            Format = FormatName,
            FormatVersion = FormatVersion,
            ExportedAtUtc = DateTimeOffset.UtcNow,
            PluginVersion = typeof(ConfigurationTransfer).Assembly.GetName().Version?.ToString() ?? "unknown",
            Configuration = configuration
        };
        WriteAtomic(path, JsonSerializer.Serialize(package, JsonOptions));
    }

    public static void ExportRoute(CustomNavigationRoute route, bool builtIn, string path)
    {
        var package = new NavigationRouteExportPackage
        {
            Format = RouteFormatName,
            FormatVersion = FormatVersion,
            ExportedAtUtc = DateTimeOffset.UtcNow,
            PluginVersion = typeof(ConfigurationTransfer).Assembly.GetName().Version?.ToString() ?? "unknown",
            Source = builtIn ? "BuiltIn" : "User",
            Route = route
        };
        WriteAtomic(path, JsonSerializer.Serialize(package, JsonOptions));
    }

    public static CustomNavigationRoute ImportRoute(string path)
    {
        var file = ValidateImportFile(path, "路线");

        NavigationRouteExportPackage? package;
        try
        {
            package = JsonSerializer.Deserialize<NavigationRouteExportPackage>(File.ReadAllText(file.FullName), JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("JSON 内容无法解析", exception);
        }

        if (package == null || package.Format != RouteFormatName || package.FormatVersion is < 1 or > FormatVersion)
            throw new InvalidDataException("这不是受支持的新月罗盘单条路线文件");

        var route = package.Route ?? throw new InvalidDataException("路线文件缺少 Route 内容");
        ValidateRoute(route);
        route.Id = Guid.NewGuid().ToString("N");
        route.EventName = route.EventName.Trim();
        return route;
    }

    public static PluginConfiguration Import(string path)
    {
        var file = ValidateImportFile(path, "配置");

        ConfigurationExportPackage? package;
        try
        {
            package = JsonSerializer.Deserialize<ConfigurationExportPackage>(File.ReadAllText(file.FullName), JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("JSON 内容无法解析", exception);
        }

        if (package == null || package.Format != FormatName || package.FormatVersion is < 1 or > FormatVersion)
            throw new InvalidDataException("这不是受支持的新月罗盘完整配置文件");
        var imported = package.Configuration ?? throw new InvalidDataException("配置文件缺少 Configuration 内容");
        if (imported.Version > PluginConfiguration.CurrentVersion)
            throw new InvalidDataException($"配置版本 {imported.Version} 高于当前支持的版本 {PluginConfiguration.CurrentVersion}");

        ValidateRoutes(imported);
        imported.Normalize();
        return imported;
    }

    public static void Apply(PluginConfiguration target, PluginConfiguration source)
    {
        foreach (var property in typeof(PluginConfiguration).GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanRead || !property.CanWrite) continue;
            property.SetValue(target, property.GetValue(source));
        }
    }

    public static PluginConfiguration Clone(PluginConfiguration source) =>
        JsonSerializer.Deserialize<PluginConfiguration>(JsonSerializer.Serialize(source, JsonOptions), JsonOptions) ??
        throw new InvalidDataException("无法创建当前配置的安全副本");

    private static void ValidateRoutes(PluginConfiguration configuration)
    {
        if (configuration.CustomNavigationRoutes == null)
            throw new InvalidDataException("配置文件缺少自定义路线列表");
        foreach (var route in configuration.CustomNavigationRoutes)
            ValidateRoute(route);
    }

    private static void ValidateRoute(CustomNavigationRoute? route)
    {
        if (route == null || string.IsNullOrWhiteSpace(route.Id) || route.EventId == 0 ||
            route.SourceAetheryteDataId == 0 || route.Points == null || route.Points.Count < 2 ||
            route.TerritoryId is not PotCandidateCatalog.SouthHornTerritoryId and
                not PotCandidateCatalog.NorthHornTerritoryId ||
            !Enum.IsDefined(route.Kind))
            throw new InvalidDataException("文件包含无效的自定义路线");
        if (route.Points.Any(point => point == null || !float.IsFinite(point.X) ||
                                      !float.IsFinite(point.Y) || !float.IsFinite(point.Z) ||
                                      !Enum.IsDefined(point.Action)))
            throw new InvalidDataException($"路线“{route.EventName}”包含无效坐标或动作");
    }

    private static FileInfo ValidateImportFile(string path, string kind)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new InvalidDataException($"选择的{kind}文件不存在");
        if (file.Length <= 0 || file.Length > MaximumImportBytes)
            throw new InvalidDataException($"{kind}文件为空或超过 64 MB");
        return file;
    }

    private static void WriteAtomic(string path, string content)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidDataException("无法确定文件目录");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, content);
            File.Move(temporary, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed class ConfigurationExportPackage
    {
        public string Format { get; set; } = string.Empty;
        public int FormatVersion { get; set; }
        public DateTimeOffset ExportedAtUtc { get; set; }
        public string PluginVersion { get; set; } = string.Empty;
        public PluginConfiguration? Configuration { get; set; }
    }

    private sealed class NavigationRouteExportPackage
    {
        public string Format { get; set; } = string.Empty;
        public int FormatVersion { get; set; }
        public DateTimeOffset ExportedAtUtc { get; set; }
        public string PluginVersion { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public CustomNavigationRoute? Route { get; set; }
    }
}
