using System.Text.Json;

namespace VIPCore.Configs;

public interface IConfig
{
    string Name { get; }
    void Load();
}

public class Config<T> : IConfig where T : new()
{
    private readonly string _path;

    public T Value { get; private set; } = default!;
    public string Name { get; }

    public Config(string path, string name)
    {
        Name = name;
        _path = path;
        Load();
    }

    // Next-il: the config is one file shared by every server on a node (Mounts bind mount). It is never written on
    // load - a missing file runs on defaults - and a read that lands while someone edits it is retried.
    public void Load()
    {
        if (!File.Exists(_path))
        {
            Value = new T();
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var json = File.ReadAllText(_path);
                if (!string.IsNullOrWhiteSpace(json) || attempt == 4)
                {
                    Value = JsonSerializer.Deserialize<T>(json, ConfigSystem.ConfigJsonOptions) ??
                            throw new JsonException($"{Name}.json cannot be deserialized");
                    return;
                }
            }
            catch (Exception e) when (e is JsonException or IOException && attempt < 4)
            {
            }

            Thread.Sleep(100);
        }
    }

    public void Save()
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(Value, ConfigSystem.ConfigJsonOptions));
    }
}
