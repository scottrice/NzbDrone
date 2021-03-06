﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.Remoting.Messaging;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using NLog;
using NzbDrone.Common;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Metadata.Files;
using NzbDrone.Core.Tv;

namespace NzbDrone.Core.Metadata.Consumers.Wdtv
{
    public class WdtvMetadata : MetadataBase<WdtvMetadataSettings>
    {
        private readonly IMapCoversToLocal _mediaCoverService;
        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;

        public WdtvMetadata(IMapCoversToLocal mediaCoverService,
                            IDiskProvider diskProvider,
                            Logger logger)
        {
            _mediaCoverService = mediaCoverService;
            _diskProvider = diskProvider;
            _logger = logger;
        }

        private static readonly Regex SeasonImagesRegex = new Regex(@"^(season (?<season>\d+))|(?<season>specials)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public override List<MetadataFile> AfterRename(Series series, List<MetadataFile> existingMetadataFiles, List<EpisodeFile> episodeFiles)
        {
            var episodeFilesMetadata = existingMetadataFiles.Where(c => c.EpisodeFileId > 0).ToList();
            var updatedMetadataFiles = new List<MetadataFile>();

            foreach (var episodeFile in episodeFiles)
            {
                var metadataFiles = episodeFilesMetadata.Where(m => m.EpisodeFileId == episodeFile.Id).ToList();

                foreach (var metadataFile in metadataFiles)
                {
                    string newFilename;

                    if (metadataFile.Type == MetadataType.EpisodeImage)
                    {
                        newFilename = GetEpisodeImageFilename(episodeFile.Path);
                    }

                    else if (metadataFile.Type == MetadataType.EpisodeMetadata)
                    {
                        newFilename = GetEpisodeMetadataFilename(episodeFile.Path);
                    }

                    else
                    {
                        _logger.Trace("Unknown episode file metadata: {0}", metadataFile.RelativePath);
                        continue;
                    }

                    var existingFilename = Path.Combine(series.Path, metadataFile.RelativePath);

                    if (!newFilename.PathEquals(existingFilename))
                    {
                        _diskProvider.MoveFile(existingFilename, newFilename);
                        metadataFile.RelativePath = series.Path.GetRelativePath(newFilename);

                        updatedMetadataFiles.Add(metadataFile);
                    }
                }
            }
            return updatedMetadataFiles;
        }

        public override MetadataFile FindMetadataFile(Series series, string path)
        {
            var filename = Path.GetFileName(path);

            if (filename == null) return null;

            var metadata = new MetadataFile
                           {
                               SeriesId = series.Id,
                               Consumer = GetType().Name,
                               RelativePath = series.Path.GetRelativePath(path)
                           };

            //Series and season images are both named folder.jpg, only season ones sit in season folders
            if (String.Compare(filename, "folder.jpg", true) == 0)
            {
                var parentdir = Directory.GetParent(path);
                var seasonMatch = SeasonImagesRegex.Match(parentdir.Name);
                if (seasonMatch.Success)
                {
                    metadata.Type = MetadataType.SeasonImage;

                    var seasonNumber = seasonMatch.Groups["season"].Value;

                    if (seasonNumber.Contains("specials"))
                    {
                        metadata.SeasonNumber = 0;
                    }
                    else
                    {
                        metadata.SeasonNumber = Convert.ToInt32(seasonNumber);
                    }

                    return metadata;
                }
                else
                {
                    metadata.Type = MetadataType.SeriesImage;
                    return metadata;
                }
            }

            var parseResult = Parser.Parser.ParseTitle(filename);

            if (parseResult != null &&
                !parseResult.FullSeason)
            {
                switch (Path.GetExtension(filename).ToLowerInvariant())
                {
                    case ".xml":
                        metadata.Type = MetadataType.EpisodeMetadata;
                        return metadata;
                    case ".metathumb":
                        metadata.Type = MetadataType.EpisodeImage;
                        return metadata;
                }
                
            }

            return null;
        }

        public override MetadataFileResult SeriesMetadata(Series series)
        {
            //Series metadata is not supported
            return null;
        }

        public override MetadataFileResult EpisodeMetadata(Series series, EpisodeFile episodeFile)
        {
            if (!Settings.EpisodeMetadata)
            {
                return null;
            }

            _logger.Debug("Generating Episode Metadata for: {0}", episodeFile.Path);

            var xmlResult = String.Empty;
            foreach (var episode in episodeFile.Episodes.Value)
            {
                var sb = new StringBuilder();
                var xws = new XmlWriterSettings();
                xws.OmitXmlDeclaration = true;
                xws.Indent = false;

                using (var xw = XmlWriter.Create(sb, xws))
                {
                    var doc = new XDocument();

                    var details = new XElement("details");
                    details.Add(new XElement("id", series.Id));
                    details.Add(new XElement("title", String.Format("{0} - {1}x{2} - {3}", series.Title, episode.SeasonNumber, episode.EpisodeNumber, episode.Title)));
                    details.Add(new XElement("series_name", series.Title));
                    details.Add(new XElement("episode_name", episode.Title));
                    details.Add(new XElement("season_number", episode.SeasonNumber));
                    details.Add(new XElement("episode_number", episode.EpisodeNumber));
                    details.Add(new XElement("firstaired", episode.AirDate));
                    details.Add(new XElement("genre", String.Join(" / ", series.Genres)));
                    details.Add(new XElement("actor", String.Join(" / ", series.Actors.ConvertAll(c => c.Name + " - " + c.Character))));
                    details.Add(new XElement("overview", episode.Overview));


                    //Todo: get guest stars, writer and director
                    //details.Add(new XElement("credits", tvdbEpisode.Writer.FirstOrDefault()));
                    //details.Add(new XElement("director", tvdbEpisode.Directors.FirstOrDefault()));

                    doc.Add(details);
                    doc.Save(xw);

                    xmlResult += doc.ToString();
                    xmlResult += Environment.NewLine;
                }
            }

            var filename = GetEpisodeMetadataFilename(episodeFile.Path);

            return new MetadataFileResult(filename, xmlResult.Trim(Environment.NewLine.ToCharArray()));
        }

        public override List<ImageFileResult> SeriesImages(Series series)
        {
            if (!Settings.SeriesImages)
            {
                return new List<ImageFileResult>();
            }

            //Because we only support one image, attempt to get the Poster type, then if that fails grab the first
            var image = series.Images.SingleOrDefault(c => c.CoverType == MediaCoverTypes.Poster) ?? series.Images.FirstOrDefault();
            if (image == null)
            {
                _logger.Trace("Failed to find suitable Series image for series {0}.", series.Title);
                return new List<ImageFileResult>();
            }

            var source = _mediaCoverService.GetCoverPath(series.Id, image.CoverType);
            var destination = Path.Combine(series.Path, "folder" + Path.GetExtension(source));

            return new List<ImageFileResult>
                   {
                       new ImageFileResult(destination, source)
                   };
        }

        public override List<ImageFileResult> SeasonImages(Series series, Season season)
        {
            if (!Settings.SeasonImages)
            {
                return new List<ImageFileResult>();
            }
            
            var seasonFolders = GetSeasonFolders(series);

            //Work out the path to this season - if we don't have a matching path then skip this season.
            string seasonFolder;
            if (!seasonFolders.TryGetValue(season.SeasonNumber, out seasonFolder))
            {
                _logger.Trace("Failed to find season folder for series {0}, season {1}.", series.Title, season.SeasonNumber);
                return new List<ImageFileResult>();
            }

            //WDTV only supports one season image, so first of all try for poster otherwise just use whatever is first in the collection
            var image = season.Images.SingleOrDefault(c => c.CoverType == MediaCoverTypes.Poster) ?? season.Images.FirstOrDefault();
            if (image == null)
            {
                _logger.Trace("Failed to find suitable season image for series {0}, season {1}.", series.Title, season.SeasonNumber);
                return new List<ImageFileResult>();
            }

            var path = Path.Combine(series.Path, seasonFolder, "folder.jpg");

            return new List<ImageFileResult>{ new ImageFileResult(path, image.Url) };
        }

        public override List<ImageFileResult> EpisodeImages(Series series, EpisodeFile episodeFile)
        {
            if (!Settings.EpisodeImages)
            {
                return new List<ImageFileResult>();
            }

            var screenshot = episodeFile.Episodes.Value.First().Images.SingleOrDefault(i => i.CoverType == MediaCoverTypes.Screenshot);

            if (screenshot == null)
            {
                _logger.Trace("Episode screenshot not available");
                return new List<ImageFileResult>();
            }

            return new List<ImageFileResult>{ new ImageFileResult(GetEpisodeImageFilename(episodeFile.Path), screenshot.Url) };
        }

        private string GetEpisodeMetadataFilename(string episodeFilePath)
        {
            return Path.ChangeExtension(episodeFilePath, "xml");
        }

        private string GetEpisodeImageFilename(string episodeFilePath)
        {
            return Path.ChangeExtension(episodeFilePath, "metathumb");
        }

        private Dictionary<Int32, String> GetSeasonFolders(Series series)
        {
            var seasonFolderMap = new Dictionary<Int32, String>();

            foreach (var folder in _diskProvider.GetDirectories(series.Path))
            {
                var directoryinfo = new DirectoryInfo(folder);
                var seasonMatch = SeasonImagesRegex.Match(directoryinfo.Name);

                if (seasonMatch.Success)
                {
                    var seasonNumber = seasonMatch.Groups["season"].Value;

                    if (seasonNumber.Contains("specials"))
                    {
                        seasonFolderMap[0] = folder;
                    }
                    else
                    {
                        int matchedSeason;
                        if (Int32.TryParse(seasonNumber, out matchedSeason))
                        {
                            seasonFolderMap[matchedSeason] = folder;
                        }
                        else
                        {
                            _logger.Debug("Failed to parse season number from {0} for series {1}.", folder, series.Title);
                        }
                    }
                }

                else
                {
                    _logger.Debug("Rejecting folder {0} for series {1}.", Path.GetDirectoryName(folder), series.Title);
                }
            }

            return seasonFolderMap;
        }
    }
}
