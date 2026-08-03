using System.Text.Json;
using System.Text.Json.Serialization;
using DicomMover.Models;

namespace DicomMover.Services;

public sealed class SettingsStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public SettingsStore(string path) => _path = Path.GetFullPath(path);

    public AppSettings Load()
    {
        if (!File.Exists(_path))
        {
            var defaults = new AppSettings();
            defaults.NormalizeLegacy();
            defaults.Validate();
            return defaults;
        }

        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions)
            ?? throw new InvalidDataException("Файл настроек пуст.");
        settings.NormalizeLegacy();
        settings.Validate();
        return settings;
    }

    public void Save(AppSettings settings)
    {
        settings.Validate();
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidDataException("Не удалось определить каталог настроек.");
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";
        var backupPath = _path + ".bak";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
        _ = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(temporaryPath), JsonOptions)
            ?? throw new InvalidDataException("Проверка настроек не пройдена.");

        if (File.Exists(_path))
            File.Replace(temporaryPath, _path, backupPath, ignoreMetadataErrors: true);
        else
            File.Move(temporaryPath, _path);
    }
}
