using System.Collections.Generic;

namespace MirrorDeck.App.Services;

public sealed record DlssInstallStatus(bool IsPresent, string Message, IReadOnlyList<string> MissingFiles);
