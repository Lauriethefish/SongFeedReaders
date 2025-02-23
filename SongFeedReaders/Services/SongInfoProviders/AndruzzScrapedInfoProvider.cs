﻿using ProtoBuf;
using SongFeedReaders.Logging;
using SongFeedReaders.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using WebUtilities;
using WebUtilities.DownloadContainers;

namespace SongFeedReaders.Services.SongInfoProviders
{
    /// <summary>
    /// Uses Andruzz's Scrapped Data to provide song information.
    /// <see href="https://github.com/andruzzzhka/BeatSaberScrappedData"/>
    /// </summary>
    public class AndruzzScrapedInfoProvider : SongInfoProviderBase
    {
        private const string ScrapedDataUrl = @"https://raw.githubusercontent.com/andruzzzhka/BeatSaberScrappedData/master/songDetails2.gz";
        private const string dataFileName = "songDetails2.gz";
        private Dictionary<string, ScrapedSong>? _byHash;
        private Dictionary<string, ScrapedSong>? _byKey;
        private readonly object _initializeLock = new object();
        private Task<bool>? initializeTask;
        /// <summary>
        /// Web client to use for web requests.
        /// </summary>
        protected readonly IWebClient WebClient;
        /// <summary>
        /// Path to the local file.
        /// </summary>
        public string? FilePath { get; set; }
        /// <summary>
        /// Maximum age of cached data.
        /// </summary>
        public TimeSpan MaxAge { get; set; } = TimeSpan.FromDays(2);
        /// <summary>
        /// If true, allow fetching new data from the web.
        /// </summary>
        public bool AllowWebFetch { get; set; } = true;
        /// <summary>
        /// If true, cache the downloaded data to disk.
        /// </summary>
        public bool CacheToDisk { get; set; }
        /// <summary>
        /// Create a new <see cref="AndruzzScrapedInfoProvider"/> with a <see cref="IWebClient"/>
        /// and <see cref="ILogFactory"/>.
        /// </summary>
        /// <param name="client"></param>
        /// <param name="logFactory"></param>
        public AndruzzScrapedInfoProvider(IWebClient client, ILogFactory? logFactory)
            : base(logFactory)
        {
            WebClient = client ?? throw new ArgumentNullException(nameof(client));
        }
        /// <summary>
        /// Create a new <see cref="AndruzzScrapedInfoProvider"/> with a location to store downloaded data,
        /// an <see cref="IWebClient"/>, and an <see cref="ILogFactory"/>.
        /// </summary>
        /// <param name="filePath"></param>
        /// <param name="client"></param>
        /// <param name="logFactory"></param>
        public AndruzzScrapedInfoProvider(string filePath, IWebClient client, ILogFactory? logFactory)
            : this(client, logFactory)
        {
            FilePath = filePath;
        }
        /// <summary>
        /// Fetches the latest scraped data and/or loads data from the disk.
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        protected Task<bool> InitializeData(CancellationToken cancellationToken)
        {
            lock (_initializeLock)
            {
                if (initializeTask == null)
                    initializeTask = InitializeDataInternal();
            }
            if (initializeTask.IsCompleted)
                return initializeTask;
            else
            {
                Task<bool>? finished = Task.Run(async () => await initializeTask.ConfigureAwait(false), cancellationToken);
                return finished;
            }
        }
        private async Task<bool> InitializeDataInternal()
        {
            AndruzzProtobufContainer? songContainer = null;
            string source = "None";
            bool fetchWeb = AllowWebFetch;
            try
            {
                string? filePath = FilePath;
                if (filePath != null && filePath.Length > 0)
                {
                    songContainer = ParseFile(filePath);
                    if (songContainer != null)
                    {
                        source = $"File|{filePath}";
                        TimeSpan dataAge = DateTime.UtcNow - songContainer.ScrapeTime;
                        if (dataAge < MaxAge)
                            fetchWeb = false;
                        else
                            Logger?.Debug($"Cached data is outdated ({songContainer.ScrapeTime:g}).");
                    }
                }

                if (fetchWeb)
                {
                    AndruzzProtobufContainer? webContainer = await ParseGzipWebSource(new Uri(ScrapedDataUrl)).ConfigureAwait(false);

                    if (webContainer != null)
                    {
                        songContainer = webContainer;
                        source = $"GitHub|{ScrapedDataUrl}";
                    }
                    else if (songContainer != null)
                        Logger?.Warning($"Unable to fetch updated data from web, using cached data.");
                }
                AndruzzProtobufSong[]? songs = songContainer?.songs;
                if (songs != null)
                {
                    CreateDictionaries(songs.Length);
                    foreach (AndruzzProtobufSong song in songs)
                    {
#pragma warning disable CS8602 // Dereference of a possibly null reference.
                        if (song.Hash != null)
                            _byHash[song.Hash] = song;
                        if (song.Key != null && song.Key.Length > 0)
                            _byKey[song.Key] = song;
#pragma warning restore CS8602 // Dereference of a possibly null reference.
                    }
                    _available = true;
                    Logger?.Debug($"{songs?.Length ?? 0} songs data loaded from '{source}', format version {songContainer?.formatVersion}, last updated {songContainer?.ScrapeTime:g}");
                }
                else
                {
                    Logger?.Warning("Unable to load Andruzz's Scrapped Data.");
                }
            }
            catch (Exception ex)
            {
                Logger?.Warning($"Error loading Andruzz's Scrapped Data: {ex.Message}");
            }
            return true;
        }


        private AndruzzProtobufContainer? ParseFile(string filePath)
        {
            AndruzzProtobufContainer? songContainer = null;
            if (filePath != null && File.Exists(filePath))
            {
                try
                {
                    using Stream? fs = File.OpenRead(filePath);
                    songContainer = ParseProtobuf(fs);
                    if (songContainer == null)
                        Logger?.Warning($"Failed to load song info protobuf file at '{filePath}'");
                }
                catch (Exception ex)
                {
                    Logger?.Warning($"Error reading song info protobuf file at '{filePath}': {ex.Message}");
                }
            }
            return songContainer;
        }

        private async Task<AndruzzProtobufContainer?> ParseGzipWebSource(Uri uri)
        {
            AndruzzProtobufContainer? songContainer = null;
            try
            {
                IWebResponseMessage? downloadResponse = await WebClient.GetAsync(uri).ConfigureAwait(false);
                downloadResponse.EnsureSuccessStatusCode();
                if (downloadResponse.Content != null)
                {
                    using Stream scrapeStream = await downloadResponse.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    string? filePath = FilePath;
                    if (CacheToDisk && !string.IsNullOrWhiteSpace(filePath))
                    {
                        using GZipStream gstream = new GZipStream(scrapeStream, CompressionMode.Decompress);
                        using MemoryDownloadContainer container = new MemoryDownloadContainer();
                        await container.ReceiveDataAsync(gstream).ConfigureAwait(false);
                        using Stream ps = container.GetResultStream();
                        songContainer = ParseProtobuf(ps);
                        try
                        {
                            using Stream s = container.GetResultStream();
                            using Stream fs = File.Create(filePath);
                            await s.CopyToAsync(fs).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Logger?.Warning($"Error caching Andruzz's scraped data to file '{filePath}': {ex.Message}");
                        }
                    }
                    else
                    {
                        using GZipStream gstream = new GZipStream(scrapeStream, CompressionMode.Decompress);
                        songContainer = ParseProtobuf(gstream);
                    }

                }
            }
            catch (Exception ex)
            {
                Logger?.Warning($"Error loading Andruzz's Scrapped Data from GitHub: {ex.Message}");
            }
            return songContainer;
        }

        private void CreateDictionaries(int size)
        {
            if (_byHash == null)
                _byHash = new Dictionary<string, ScrapedSong>(size, StringComparer.OrdinalIgnoreCase);
            if (_byKey == null)
                _byKey = new Dictionary<string, ScrapedSong>(size, StringComparer.OrdinalIgnoreCase);
        }

        private static AndruzzProtobufContainer? ParseProtobuf(Stream protobufStream)
        {
            return Serializer.Deserialize<AndruzzProtobufContainer>(protobufStream);
        }

        private bool _available = false;
        /// <inheritdoc/>
        public override bool Available => _available;

        /// <inheritdoc/>
        public override async Task<ScrapedSong?> GetSongByHashAsync(string hash, CancellationToken cancellationToken)
        {
            await InitializeData(cancellationToken).ConfigureAwait(false);
            ScrapedSong? song = null;
            _byHash?.TryGetValue(hash, out song);
            return song;
        }

        /// <inheritdoc/>
        public override async Task<ScrapedSong?> GetSongByKeyAsync(string key, CancellationToken cancellationToken)
        {
            await InitializeData(cancellationToken).ConfigureAwait(false);
            ScrapedSong? song = null;
            _byKey?.TryGetValue(key, out song);
            return song;
        }
    }
}
