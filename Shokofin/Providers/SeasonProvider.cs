using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using Shokofin.API;
using Shokofin.ExternalIds;
using Shokofin.Utils;

using Info = Shokofin.API.Info;

namespace Shokofin.Providers;

public class SeasonProvider : IRemoteMetadataProvider<Season, SeasonInfo>, IHasOrder
{
    public string Name => Plugin.MetadataProviderName;

    public int Order => 0;

    private readonly IHttpClientFactory HttpClientFactory;

    private readonly ILogger<SeasonProvider> Logger;

    private readonly ShokoAPIManager ApiManager;

    public SeasonProvider(IHttpClientFactory httpClientFactory, ILogger<SeasonProvider> logger, ShokoAPIManager apiManager)
    {
        HttpClientFactory = httpClientFactory;
        Logger = logger;
        ApiManager = apiManager;
    }

    public async Task<MetadataResult<Season>> GetMetadata(SeasonInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<Season>();
        if (!info.IndexNumber.HasValue)
            return result;

        // Special handling of the "Specials" season (pun intended).
        if (info.IndexNumber.Value == 0) {
            // We're forcing the sort names to start with "AA" to make it
            // always appear first in the UI.
            var seasonName = info.Name;
            result.Item = new Season {
                Name = seasonName,
                IndexNumber = info.IndexNumber,
                SortName = $"AA - {seasonName}",
                ForcedSortName = $"AA - {seasonName}",
            };
            result.HasMetadata = true;

            return result;
        }

        if (!info.SeriesProviderIds.TryGetValue(ShokoSeriesId.Name, out var seriesId) || !info.IndexNumber.HasValue) {
            Logger.LogDebug("Unable refresh Season {SeasonNumber} {SeasonName}", info.IndexNumber, info.Name);
            return result;
        }

        var seasonNumber = info.IndexNumber.Value;
        var trackerId = Plugin.Instance.Tracker.Add($"Providing info for Season \"{info.Name}\". (Path=\"{info.Path}\",Series=\"{seriesId}\",Season={seasonNumber})");
        try {
            var showInfo = await ApiManager.GetShowInfoForSeries(seriesId);
            if (showInfo == null) {
                Logger.LogWarning("Unable to find show info for Season {SeasonNumber}. (Series={SeriesId})", seasonNumber, seriesId);
                return result;
            }

            var seasonInfo = showInfo.GetSeasonInfoBySeasonNumber(seasonNumber);
            if (seasonInfo == null || !showInfo.TryGetBaseSeasonNumberForSeasonInfo(seasonInfo, out var baseSeasonNumber)) {
                Logger.LogWarning("Unable to find series info for Season {SeasonNumber}. (Series={SeriesId},Group={GroupId})", seasonNumber, seriesId, showInfo.GroupId);
                return result;
            }

            Logger.LogInformation("Found info for Season {SeasonNumber} in Series {SeriesName} (Series={SeriesId},Group={GroupId})", seasonNumber, showInfo.Name, seriesId, showInfo.GroupId);

            var offset = Math.Abs(seasonNumber - baseSeasonNumber);

            result.Item = CreateMetadata(seasonInfo, seasonNumber, offset, info.MetadataLanguage, info.MetadataCountryCode);
            result.HasMetadata = true;
            result.ResetPeople();
            foreach (var person in seasonInfo.Staff)
                result.AddPerson(person);

            return result;
        }
        catch (Exception ex) {
            Logger.LogError(ex, "Threw unexpectedly while refreshing season {SeasonNumber}; {Message} (Path={Path},Series={SeriesId})", info.IndexNumber, ex.Message, info.Path, seriesId);
            return new MetadataResult<Season>();
        }
        finally {
            Plugin.Instance.Tracker.Remove(trackerId);
        }
    }

    public static Season CreateMetadata(Info.SeasonInfo seasonInfo, int seasonNumber, int offset, string metadataLanguage, string metadataCountryCode)
        => CreateMetadata(seasonInfo, seasonNumber, offset, metadataLanguage, metadataCountryCode, null, Guid.Empty);

    public static Season CreateMetadata(Info.SeasonInfo seasonInfo, int seasonNumber, int offset, Series series, Guid seasonId)
        => CreateMetadata(seasonInfo, seasonNumber, offset, series.GetPreferredMetadataLanguage(), series.GetPreferredMetadataCountryCode(), series, seasonId);

    private static Season CreateMetadata(Info.SeasonInfo seasonInfo, int seasonNumber, int offset, string metadataLanguage, string metadataCountryCode, Series? series, Guid seasonId)
    {
        var (displayTitle, alternateTitle) = Text.GetSeasonTitles(seasonInfo, offset, metadataLanguage);
        var sortTitle = $"S{seasonNumber} - {seasonInfo.Shoko.Name}";
        Season season;
        if (series != null) {
            season = new Season {
                Name = displayTitle,
                OriginalTitle = alternateTitle,
                IndexNumber = seasonNumber,
                SortName = sortTitle,
                ForcedSortName = sortTitle,
                Id = seasonId,
                IsVirtualItem = true,
                Overview = Text.GetDescription(seasonInfo, metadataLanguage),
                PremiereDate = seasonInfo.AniDB.AirDate,
                EndDate = seasonInfo.AniDB.EndDate,
                ProductionYear = seasonInfo.AniDB.AirDate?.Year,
                Tags = seasonInfo.Tags.ToArray(),
                Genres = seasonInfo.Genres.ToArray(),
                Studios = seasonInfo.Studios.ToArray(),
                ProductionLocations = TagFilter.GetSeasonProductionLocations(seasonInfo),
                OfficialRating = ContentRating.GetSeasonContentRating(seasonInfo, metadataCountryCode),
                CommunityRating = seasonInfo.AniDB.Rating?.ToFloat(10),
                SeriesId = series.Id,
                SeriesName = series.Name,
                SeriesPresentationUniqueKey = series.GetPresentationUniqueKey(),
                DateModified = DateTime.UtcNow,
                DateLastSaved = DateTime.UtcNow,
            };
        }
        else {
            season = new Season {
                Name = displayTitle,
                OriginalTitle = alternateTitle,
                IndexNumber = seasonNumber,
                SortName = sortTitle,
                ForcedSortName = sortTitle,
                Overview = Text.GetDescription(seasonInfo, metadataLanguage),
                PremiereDate = seasonInfo.AniDB.AirDate,
                EndDate = seasonInfo.AniDB.EndDate,
                ProductionYear = seasonInfo.AniDB.AirDate?.Year,
                Tags = seasonInfo.Tags.ToArray(),
                Genres = seasonInfo.Genres.ToArray(),
                Studios = seasonInfo.Studios.ToArray(),
                ProductionLocations = TagFilter.GetSeasonProductionLocations(seasonInfo),
                OfficialRating = ContentRating.GetSeasonContentRating(seasonInfo, metadataCountryCode),
                CommunityRating = seasonInfo.AniDB.Rating?.ToFloat(10),
            };
        }
        season.SetProviderId(ShokoSeriesId.Name, seasonInfo.Id);

        if (Plugin.Instance.Configuration.AddAniDBId)
            season.SetProviderId("AniDB", seasonInfo.AniDB.Id.ToString());

        return season;
    }

    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeasonInfo searchInfo, CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<RemoteSearchResult>>([]);

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        => HttpClientFactory.CreateClient().GetAsync(url, cancellationToken);
}

