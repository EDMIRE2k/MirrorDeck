using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MirrorDeck.Core.Models;
using MirrorDeck.Core.Services;

namespace MirrorDeck.Settings.Persistence;

public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
	private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
	{
		WriteIndented = true,
		Converters = { (JsonConverter)new JsonStringEnumConverter() }
	};

	public string SettingsPath { get; }

	public JsonSettingsStore(string settingsPath)
	{
		SettingsPath = settingsPath;
	}

	public static JsonSettingsStore CreateDefault()
	{
		return new JsonSettingsStore(MirrorDeckStorage.GetSettingsPath());
	}

	public async Task<MirrorDeckSettings> LoadAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		if (!File.Exists(SettingsPath))
		{
			return new MirrorDeckSettings();
		}
		MirrorDeckSettings result;
		await using (FileStream stream = File.OpenRead(SettingsPath))
		{
			result = (await JsonSerializer.DeserializeAsync<MirrorDeckSettings>(stream, SerializerOptions, cancellationToken).ConfigureAwait(continueOnCapturedContext: false)) ?? new MirrorDeckSettings();
		}
		return result;
	}

	public async Task SaveAsync(MirrorDeckSettings settings, CancellationToken cancellationToken = default(CancellationToken))
	{
        // Snapshot before yielding: UI edits must not mutate an in-flight save.
        string json = JsonSerializer.Serialize(settings, SerializerOptions);
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string temporary = SettingsPath + ".saving";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            await File.WriteAllTextAsync(temporary, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, SettingsPath, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            finally { _saveGate.Release(); }
        }
	}
}
