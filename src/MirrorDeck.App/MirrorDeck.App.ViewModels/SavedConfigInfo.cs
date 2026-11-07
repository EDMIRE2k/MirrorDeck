using System;

namespace MirrorDeck.App.ViewModels;

public sealed class SavedConfigInfo
{
	public string Name { get; }

	public string Path { get; }

	public DateTimeOffset Updated { get; }

	public string UpdatedText => $"Updated {Updated.LocalDateTime:g}";

	public SavedConfigInfo(string name, string path, DateTimeOffset updated)
	{
		Name = name;
		Path = path;
		Updated = updated;
	}
}
