﻿// Copyright 2017 Carnegie Mellon University. All Rights Reserved. See LICENSE.md file for terms.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using ghosts.client.linux.Infrastructure;
using ghosts.client.linux.timelineManager;
using Ghosts.Domain;
using Ghosts.Domain.Code;
using Ghosts.Domain.Messages.MesssagesForServer;
using Newtonsoft.Json;
using NLog;

namespace ghosts.client.linux.Communications
{
    /// <summary>
    /// Get updates from the C2 server - could be timeline, health, etc.
    /// </summary>
    public static class Updates
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Threaded calls to C2 for updates and to post this client's results of activity
        /// </summary>
        public static void Run()
        {
            new Thread(() =>
            {
                Thread.CurrentThread.IsBackground = true;
                GetServerUpdates();
            }).Start();

            new Thread(() =>
            {
                Thread.CurrentThread.IsBackground = true;
                PostClientResults();
            }).Start();
        }

        private static void GetServerUpdates()
        {
            if (!Program.Configuration.ClientUpdates.IsEnabled)
                return;

            var machine = new ResultMachine();

            Thread.Sleep(Program.Configuration.ClientUpdates.CycleSleep);

            while (true)
            {
                try
                {
                    var s = string.Empty;
                    using (var client = WebClientBuilder.Build(machine))
                    {
                        try
                        {
                            using var reader =
                                new StreamReader(client.OpenRead(Program.Configuration.ClientUpdates.PostUrl));
                            s = reader.ReadToEnd();
                            _log.Debug($"{DateTime.Now} - Received new configuration");
                        }
                        catch (WebException wex)
                        {
                            if (wex?.Response == null)
                            {
                                _log.Debug($"{DateTime.Now} - API Server appears to be not responding");
                            }
                            else if (((HttpWebResponse)wex.Response).StatusCode == HttpStatusCode.NotFound)
                            {
                                _log.Debug($"{DateTime.Now} - No new configuration found");
                            }
                        }
                        catch (Exception e)
                        {
                            _log.Error(e);
                        }
                    }

                    if (!string.IsNullOrEmpty(s))
                    {
                        var update = JsonConvert.DeserializeObject<UpdateClientConfig>(s);

                        // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
                        switch (update.Type)
                        {
                            case UpdateClientConfig.UpdateType.RequestForTimeline:
                                PostCurrentTimeline(update);
                                break;
                            case UpdateClientConfig.UpdateType.Timeline:
                                TimelineBuilder.SetLocalTimeline(update.Update.ToString());
                                break;
                            case UpdateClientConfig.UpdateType.TimelinePartial:
                                try
                                {
                                    var timeline = JsonConvert.DeserializeObject<Timeline>(update.Update.ToString());

                                    foreach (var timelineHandler in timeline.TimeLineHandlers)
                                    {
                                        _log.Trace($"PartialTimeline found: {timelineHandler.HandlerType}");

                                        foreach (var timelineEvent in timelineHandler.TimeLineEvents.Where(timelineEvent => string.IsNullOrEmpty(timelineEvent.TrackableId)))
                                        {
                                            timelineEvent.TrackableId = Guid.NewGuid().ToString();
                                        }

                                        Orchestrator.RunCommand(timeline, timelineHandler);
                                    }
                                }
                                catch (Exception exc)
                                {
                                    _log.Debug(exc);
                                }

                                break;
                            case UpdateClientConfig.UpdateType.Health:
                            {
                                var newTimeline = JsonConvert.DeserializeObject<ResultHealth>(update.Update.ToString());
                                //save to local disk
                                using var file = File.CreateText(ApplicationDetails.ConfigurationFiles.Health);
                                var serializer = new JsonSerializer
                                {
                                    Formatting = Formatting.Indented
                                };
                                serializer.Serialize(file, newTimeline);

                                break;
                            }
                            default:
                            {
                                _log.Debug($"Update {update.Type} has no handler, ignoring...");
                                break;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    _log.Debug("Problem polling for new configuration");
                    _log.Error(e);
                }

                Thread.Sleep(Program.Configuration.ClientUpdates.CycleSleep);
            }
        }
        
        private static void PostCurrentTimeline(UpdateClientConfig update)
        {
            // is the config for a specific timeline id?
            var timelineId = TimelineUpdateClientConfigManager.GetConfigUpdateTimelineId(update);

            // get all timelines
            var localTimelines = TimelineManager.GetLocalTimelines();

            var timelines = localTimelines as Timeline[] ?? localTimelines.ToArray();
            if (timelineId != Guid.Empty)
            {
                foreach (var timeline in timelines)
                {
                    if (timeline.Id == timelineId)
                    {
                        timelines = new List<Timeline>()
                        {
                            timeline
                        }.ToArray();
                        break;
                    }
                }
            }


            ServicePointManager.ServerCertificateValidationCallback += (sender, cert, chain, sslPolicyErrors) => true;

            var posturl = string.Empty;

            try
            {
                posturl = Program.Configuration.IdUrl.Replace("clientid", "clienttimeline");
            }
            catch
            {
                _log.Error("Can't get timeline posturl!");
                return;
            }

            foreach (var timeline in timelines)
            {
                try
                {
                    _log.Trace("posting timeline");

                    var payload = TimelineBuilder.TimelineToString(timeline);
                    var machine = new ResultMachine();
                    // GuestInfoVars.Load(machine); // TODO?

                    using (var client = WebClientBuilder.Build(machine))
                    {
                        client.Headers[HttpRequestHeader.ContentType] = "application/json";
                        client.UploadString(posturl, JsonConvert.SerializeObject(payload));
                    }

                    _log.Trace($"{DateTime.Now} - timeline posted to server successfully");
                }
                catch (Exception e)
                {
                    _log.Debug($"Problem posting timeline to server from {ApplicationDetails.ConfigurationFiles.Timeline} to {posturl}");
                    _log.Error(e);
                }
            }
        }

        private static void PostClientResults()
        {
            if (!Program.Configuration.ClientResults.IsEnabled)
                return;

            var fileName = ApplicationDetails.LogFiles.ClientUpdates;
            var cycleSleep = Program.Configuration.ClientResults.CycleSleep;
            var postUrl = Program.Configuration.ClientResults.PostUrl;

            var machine = new ResultMachine();

            Thread.Sleep(cycleSleep);

            while (true)
            {
                try
                {
                    if(File.Exists(fileName))
                    {
                        PostResults(fileName, machine, postUrl);
                    }
                    else
                    {
                        _log.Trace($"{DateTime.Now} - {fileName} not found - sleeping...");
                    }
                }
                catch (Exception e)
                {
                    _log.Error($"Problem posting logs to server {e}");
                }

                // look for other result files that have not been posted
                try
                {
                    foreach(var file in Directory.GetFiles(Path.GetDirectoryName(fileName)))
                    {
                        if (!file.EndsWith("app.log") && file != fileName)
                        {
                            PostResults(file, machine, postUrl, true);
                        }
                    }
                }
                catch (Exception e)
                {
                    _log.Debug($"Problem posting overflow logs from {fileName} to server {postUrl}: {e}");
                }

                Thread.Sleep(cycleSleep);
            }
        }

        private static void PostResults(string fileName, ResultMachine machine, string postUrl, bool isDeletable = false)
        {
            var sb = new StringBuilder();
            var data = File.ReadLines(fileName);
            foreach (var d in data)
            {
                sb.AppendLine(d);
            }

            var r = new TransferLogDump
            {
                Log = sb.ToString()
            };

            var payload = JsonConvert.SerializeObject(r);

            if (Program.Configuration.ClientResults.IsSecure)
            {
                payload = Crypto.EncryptStringAes(payload, machine.Name);
                payload = Base64Encoder.Base64Encode(payload);

                var p = new EncryptedPayload
                {
                    Payload = payload
                };

                payload = JsonConvert.SerializeObject(p);
            }

            using (var client = WebClientBuilder.Build(machine))
            {
                client.Headers[HttpRequestHeader.ContentType] = "application/json";
                client.UploadString(postUrl, payload);
            }

            if (isDeletable)
            {
                File.Delete(fileName);
            }
            else
            {
                File.WriteAllText(fileName, string.Empty);
            }

            _log.Trace($"{DateTime.Now} - {fileName} posted to server successfully");
        }
    }
}
