using System;
using System.Windows.Forms;

namespace MirrorDeck.PreviewHost;

internal static class Program
{
	[STAThread]
	private static void Main(string[] args)
	{
		PreviewHostOptions options = PreviewHostOptions.Parse(args);
		using PreviewHostLogger previewHostLogger = PreviewHostLogger.Create();
		ReShadeBootstrapper.Initialize(previewHostLogger.Info);
		ApplicationConfiguration.Initialize();
		Application.Run(new PreviewForm(options, previewHostLogger));
	}
}
