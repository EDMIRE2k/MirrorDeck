namespace MirrorDeck.Rendering.Layout;

public readonly record struct DisplaySize(double Width, double Height)
{
	public bool IsEmpty
	{
		get
		{
			if (!(Width <= 0.0))
			{
				return Height <= 0.0;
			}
			return true;
		}
	}
}
