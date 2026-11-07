using System.Threading;
using System.Threading.Tasks;
using MirrorDeck.Core.Models;

namespace MirrorDeck.Core.Services;

public interface ISettingsStore
{
	string SettingsPath { get; }

	Task<MirrorDeckSettings> LoadAsync(CancellationToken cancellationToken = default(CancellationToken));

	Task SaveAsync(MirrorDeckSettings settings, CancellationToken cancellationToken = default(CancellationToken));
}
