namespace MirrorDeck.App.ViewModels;

public sealed record ProcessingResolutionOption(string Name, int Height)
{
	public override string ToString()
	{
		return Name;
	}
}
