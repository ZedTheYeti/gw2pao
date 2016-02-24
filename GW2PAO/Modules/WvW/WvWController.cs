﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GW2PAO.API.Data;
using GW2PAO.API.Data.Enums;
using GW2PAO.API.Services.Interfaces;
using GW2PAO.API.Util;
using GW2PAO.Data;
using GW2PAO.Data.UserData;
using GW2PAO.Modules.WvW.Interfaces;
using GW2PAO.Modules.WvW.ViewModels;
using GW2PAO.Utility;
using GW2PAO.ViewModels;
using Microsoft.Practices.Prism.Mvvm;
using NLog;
using System.Collections.Concurrent;

namespace GW2PAO.Modules.WvW
{
    [Export(typeof(IWvWController))]
    public class WvWController : IWvWController
    {
        /// <summary>
        /// Default logger
        /// </summary>
        private static Logger logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Service responsible for WvW information
        /// </summary>
        private IWvWService wvwService;

        /// <summary>
        /// Service responsible for Player information
        /// </summary>
        private IPlayerService playerService;

        /// <summary>
        /// Service responsible for retrieving Guild information
        /// </summary>
        private IGuildService guildService;

        /// <summary>
        /// Keeps track of how many times Start() has been called in order
        /// to support reuse of a single object
        /// </summary>
        private int startCallCount;

        /// <summary>
        /// The current match id monitored by the controller
        /// </summary>
        public string MatchID { get; private set; }

        /// <summary>
        /// The objectives refresh timer object
        /// </summary>
        private Timer objectivesRefreshTimer;

        /// <summary>
        /// Locking object for operations performed with the objectivesRefreshTimerLock
        /// </summary>
        private readonly object objectivesRefreshTimerLock = new object();

        /// <summary>
        /// True if the controller's timers are no longer running, else false
        /// </summary>
        private bool isStopped;

        /// <summary>
        /// Timer counter used for reducing the amount of requests performed
        /// See RefreshObjectives() for more details
        /// </summary>
        private int objectivesRefreshCounter;

        /// <summary>
        /// Timer counter used for determining when to request new world scores
        /// </summary>
        private int scoresRefreshCounter;

        /// <summary>
        /// User data for WvW
        /// </summary>
        private WvWUserData userData;

        /// <summary>
        /// Previous WvW map
        /// </summary>
        private WvWMap prevMap;

        /// <summary>
        /// Object containing the current map information
        /// </summary>
        private IHasWvWMap currentMap;

        /// <summary>
        /// The player's current WvWMap
        /// </summary>
        private WvWMap PlayerMap
        {
            get
            {
                var currentMapId = this.playerService.MapId;
                switch (currentMapId)
                {
                    case WvWMapIDs.EternalBattlegrounds:
                        return WvWMap.EternalBattlegrounds;
                    case WvWMapIDs.RedBorderlands:
                        return WvWMap.RedBorderlands;
                    case WvWMapIDs.GreenBorderlands:
                        return WvWMap.GreenBorderlands;
                    case WvWMapIDs.BlueBorderlands:
                        return WvWMap.BlueBorderlands;
                    default:
                        return WvWMap.Unknown;
                }
            }
        }

        /// <summary>
        /// Map with which to override the player map.
        /// To disable the override, set this to Unknown
        /// </summary>
        public WvWMap MapOverride
        {
            get { return this.UserData.MapOverride; }
            set { this.UserData.MapOverride = value; }
        }

        /// <summary>
        /// The interval by which to refresh the objectives state
        /// </summary>
        public int ObjectivesRefreshInterval { get; set; }

        /// <summary>
        /// The WvW user settings
        /// </summary>
        public WvWUserData UserData { get { return this.userData; } }

        /// <summary>
        /// Backing store of the teams collection
        /// </summary>
        private ObservableCollection<WvWTeamViewModel> worlds = new ObservableCollection<WvWTeamViewModel>();

        /// <summary>
        /// The collection of WvW Teams
        /// </summary>
        public ObservableCollection<WvWTeamViewModel> Worlds { get { return this.worlds; } }

        /// <summary>
        /// Backing store of the All WvW Objectives collection
        /// </summary>
        private ObservableCollection<WvWObjectiveViewModel> allobjectives = new ObservableCollection<WvWObjectiveViewModel>();

        /// <summary>
        /// The collection of All WvW Objectives
        /// </summary>
        public ObservableCollection<WvWObjectiveViewModel> AllObjectives { get { return this.allobjectives; } }

        /// <summary>
        /// Backing store of the current WvW Objectives collection
        /// </summary>
        private ObservableCollection<WvWObjectiveViewModel> currentObjectives = new ObservableCollection<WvWObjectiveViewModel>();

        /// <summary>
        /// The collection of current WvW Objectives
        /// </summary>
        public ObservableCollection<WvWObjectiveViewModel> CurrentObjectives { get { return this.currentObjectives; } }

        /// <summary>
        /// Backing store of the WvW Notifications collection
        /// </summary>
        private ObservableCollection<WvWObjectiveViewModel> wvwNotifications = new ObservableCollection<WvWObjectiveViewModel>();

        /// <summary>
        /// The collection of WvW Objective Notifications
        /// </summary>
        public ObservableCollection<WvWObjectiveViewModel> WvWNotifications { get { return this.wvwNotifications; } }

        /// <summary>
        /// Default constructor
        /// </summary>
        /// <param name="dungeonsService">The dungeons service object</param>
        /// <param name="userData">The dungeons user data object</param>
        [ImportingConstructor]
        public WvWController(IWvWService wvwService, IPlayerService playerService, IHasWvWMap hasMap, IGuildService guildService, WvWUserData userData)
        {
            logger.Debug("Initializing WvW Controller");
            this.wvwService = wvwService;
            this.playerService = playerService;
            this.guildService = guildService;
            this.currentMap = hasMap;
            this.userData = userData;
            this.objectivesRefreshCounter = 0;
            this.scoresRefreshCounter = 0;
            this.isStopped = false;

            // Initialize the refresh timer
            this.objectivesRefreshTimer = new Timer(this.Refresh);
            this.ObjectivesRefreshInterval = 500;

            // Initialize the start call count to 0
            this.startCallCount = 0;

            // Initialize the collections, but do it on a seperate thread since it can take a little time
            logger.Info("WvW Controller initialized");
        }

        /// <summary>
        /// Starts the automatic refresh
        /// </summary>
        public void Start()
        {
            logger.Debug("Start called");
            Task.Factory.StartNew(() =>
            {
                // Start the timer if this is the first time that Start() has been called
                if (this.startCallCount == 0)
                {
                    // Make sure the WvW Service has loaded the world names
                    this.wvwService.LoadData();

                    this.isStopped = false;

                    // Initialize Teams and Objectives
                    this.InitializeWorlds();
                    this.InitializeAllObjectivesCollection();

                    // Then start the timer
                    logger.Debug("Starting refresh timers");
                    this.Refresh();
                }

                this.startCallCount++;
                logger.Debug("startCallCount = {0}", this.startCallCount);

            }, TaskCreationOptions.LongRunning);
        }

        /// <summary>
        /// Stops the automatic refresh
        /// </summary>
        public void Stop()
        {
            this.startCallCount--;
            logger.Debug("Stop called - startCallCount = {0}", this.startCallCount);

            // Stop the refresh timer if all calls to Start() have had a matching call to Stop()
            if (this.startCallCount == 0)
            {
                logger.Debug("Stopping refresh timers");
                lock (this.objectivesRefreshTimerLock)
                {
                    this.isStopped = true;
                    this.objectivesRefreshTimer.Change(Timeout.Infinite, Timeout.Infinite);
                }
            }
        }

        /// <summary>
        /// Forces a shutdown of the controller, including all running timers/threads
        /// </summary>
        public void Shutdown()
        {
            logger.Debug("Shutdown called");
            logger.Debug("Stopping refresh timers");
            lock (this.objectivesRefreshTimerLock)
            {
                this.isStopped = true;
                this.objectivesRefreshTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }

        /// <summary>
        /// Initializes the WvW teams collection
        /// </summary>
        private void InitializeWorlds()
        {
            lock (objectivesRefreshTimerLock)
            {
                logger.Debug("Initializing worlds");

                Threading.InvokeOnUI(() => this.Worlds.Clear());

                var matchIDs = this.wvwService.GetMatchIDs();
                var teamColors = this.wvwService.GetTeamColors();

                foreach (var world in this.wvwService.Worlds)
                {
                    var team = new WvWTeamViewModel(world);
                    if (matchIDs.ContainsKey(team.WorldId))
                        team.MatchId = matchIDs[team.WorldId];
                    if (teamColors.ContainsKey(team.WorldId))
                        team.Color = teamColors[team.WorldId];
                    Threading.InvokeOnUI(() => this.Worlds.Add(team));
                }

                // Launch a task to refresh world score
                // Do this with a background task so we don't slow down initialization (this is not critical)
                Task.Factory.StartNew(this.RefreshWorldScores);
            }
        }

        /// <summary>
        /// Initializes the All Objectives collection
        /// </summary>
        private void InitializeAllObjectivesCollection()
        {
            lock (objectivesRefreshTimerLock)
            {
                logger.Debug("Initializing objectives");

                Threading.InvokeOnUI(() => this.AllObjectives.Clear());

                // Determine the current match. If this changes, we don't need to re-initialize since the actual objectives don't change - just the owners change
                var matchID = this.wvwService.GetMatchId(this.UserData.WorldSelection.ID);
                var objectives = this.wvwService.GetAllObjectives(matchID);

                while (objectives.Count() == 0 && this.startCallCount > 0)
                {
                    // If we started up while in the middle of a reset, the objectives count will return 0, so loop until we get it
                    Thread.Sleep(1000);
                    matchID = this.wvwService.GetMatchId(this.UserData.WorldSelection.ID);
                    objectives = this.wvwService.GetAllObjectives(matchID);
                }

                Threading.InvokeOnUI(() =>
                {
                    foreach (var obj in objectives)
                    {
                        logger.Debug("Initializing view model for {0} - {1}", obj.Name, obj.Map);
                        var vm = new WvWObjectiveViewModel(obj, this.UserData, this.Worlds, this.WvWNotifications);
                        this.AllObjectives.Add(vm);
                    }
                });
            }
        }

        /// <summary>
        /// Rebuilds the current objectives collections
        /// </summary>
        private void RebuildCurrentObjectivesCollection(WvWMap map)
        {
            logger.Debug("Building objectives collection");

            Threading.InvokeOnUI(() =>
            {
                this.CurrentObjectives.Clear();
                foreach (var objective in this.AllObjectives.Where(obj => obj.Map == map))
                {
                    this.CurrentObjectives.Add(objective);
                }
            });
        }

        /// <summary>
        /// Refreshes all objectives within the objectives collection
        /// This is the primary function of the WvWController
        /// </summary>
        private void Refresh(object state = null)
        {
            lock (this.objectivesRefreshTimerLock)
            {
                if (this.isStopped)
                    return; // Immediately return if we are supposed to be stopped

                var matchID = this.wvwService.GetMatchId(this.UserData.WorldSelection.ID);
                if (this.MatchID != matchID)
                {
                    this.HandleMatchChange(matchID);
                }
                else
                {
                    // Check for new WvW Map
                    this.CheckForMapChange();

                    // Refresh state of all objectives and refresh the match scores
                    // Do this only once every 2 seconds
                    this.objectivesRefreshCounter++;
                    if (this.objectivesRefreshCounter >= 4) // 500ms * 4 = 2seconds
                    {
                        this.objectivesRefreshCounter = 0;
                        this.RefreshObjectives();
                    }

                    this.scoresRefreshCounter++;
                    if (this.scoresRefreshCounter >= 60) // 500ms * 20 = 30seconds
                    {
                        this.scoresRefreshCounter = 0;
                        Task.Factory.StartNew(this.RefreshWorldScores);
                    }

                    this.RefreshTimers();

                    // Calculate distances
                    this.CalculateDistances();
                }
                this.objectivesRefreshTimer.Change(this.ObjectivesRefreshInterval, Timeout.Infinite);
            }
        }

        /// <summary>
        /// Performs actions to handle a match change (either the match ended or the user switched matches)
        /// </summary>
        /// <param name="newMatchID">The new match ID</param>
        private void HandleMatchChange(string newMatchID)
        {
            logger.Info("Match change detected: new matchID = {0}", newMatchID);
            this.MatchID = newMatchID;

            if (MatchID == null)
            {
                // Unable to retrieve the current match ID, which means a reset is probably occuring
                // When this happens, clear out the state of everything
                Threading.InvokeOnUI(() =>
                {
                    foreach (var objective in this.AllObjectives)
                    {
                        objective.PrevWorldOwner = WorldColor.None;
                        objective.WorldOwner = WorldColor.None;
                        objective.FlipTime = DateTime.UtcNow;
                        objective.DistanceFromPlayer = 0;
                        objective.TimerValue = TimeSpan.Zero;
                        objective.IsRIActive = false;
                        objective.GuildClaimer.ID = null;
                        objective.GuildClaimer.Name = string.Empty;
                        objective.GuildClaimer.Tag = string.Empty;
                    }
                });
            }
            else
            {
                // Refresh all team colors
                var teamColors = this.wvwService.GetTeamColors();
                if (teamColors.Count == this.Worlds.Count)
                {
                    Threading.InvokeOnUI(() =>
                    {
                        foreach (var team in this.Worlds)
                        {
                            team.Color = teamColors[team.WorldId];
                        }
                    });

                    // Refresh state of all objectives
                    var latestObjectivesData = this.wvwService.GetAllObjectives(MatchID);
                    while (latestObjectivesData.Count() != this.AllObjectives.Count
                        && !this.isStopped)
                    {
                        // We were unable to pull data for all objectives - this can happen if we are
                        // in the middle of a reset. As such, loop until we actually get a full set
                        logger.Warn("Unable to retrieve data for all objectives! Trying again...");
                        latestObjectivesData = this.wvwService.GetAllObjectives(MatchID);
                    }

                    ConcurrentDictionary<Guid, API.Data.Entities.Guild> guildDict = new ConcurrentDictionary<Guid, API.Data.Entities.Guild>();
                    Parallel.ForEach(latestObjectivesData.Where(o => o.GuildOwner.HasValue), (objective) =>
                    {
                        var guildInfo = this.guildService.GetGuild(objective.GuildOwner.Value);
                        guildDict.TryAdd(guildInfo.ID, guildInfo);
                    });

                    if (latestObjectivesData.Count() >= this.AllObjectives.Count)
                    {
                        Threading.InvokeOnUI(() =>
                        {
                            foreach (var objective in this.AllObjectives)
                            {
                                objective.RefreshForMatchReset(this.Worlds);
                                var latestData = latestObjectivesData.First(obj => obj.ID == objective.ID && obj.Map == objective.Map);
                                objective.ModelData.MatchId = this.MatchID;
                                objective.PrevWorldOwner = latestData.WorldOwner;
                                objective.WorldOwner = latestData.WorldOwner;
                                objective.FlipTime = DateTime.UtcNow;
                                objective.DistanceFromPlayer = 0;
                                objective.TimerValue = TimeSpan.Zero;
                                objective.IsRIActive = false;

                                if (latestData.GuildOwner.HasValue)
                                {
                                    objective.GuildClaimer.ID = latestData.GuildOwner.Value;
                                    API.Data.Entities.Guild guildInfo;
                                    if (guildDict.TryGetValue(objective.GuildClaimer.ID.Value, out guildInfo)
                                        && guildInfo != null)
                                    {
                                        objective.GuildClaimer.Name = guildInfo.Name;
                                        objective.GuildClaimer.Tag = string.Format("[{0}]", guildInfo.Tag);
                                    }
                                }
                            }
                        });
                    }
                }
            }
        }

        /// <summary>
        /// Performs a check for a map change and performs any neccessary actions if the map has changed
        /// </summary>
        private void CheckForMapChange()
        {
            if (this.MapOverride != WvWMap.Unknown)
            {
                if (this.MapOverride != this.prevMap)
                {
                    // Map changed, rebuild the objectives
                    this.prevMap = this.MapOverride;
                    this.currentMap.Map = this.MapOverride;
                    this.RebuildCurrentObjectivesCollection(this.MapOverride);
                }
            }
            else
            {
                if (this.PlayerMap != this.prevMap)
                {
                    // Map changed, rebuild the objectives
                    this.prevMap = this.PlayerMap;
                    this.currentMap.Map = this.PlayerMap;
                    this.RebuildCurrentObjectivesCollection(this.PlayerMap);
                }
            }
        }
        
        /// <summary>
        /// Refreshes various state information for all objectives
        /// </summary>
        private void RefreshObjectives()
        {
            var latestObjectivesData = this.wvwService.GetAllObjectives(MatchID);
            if (latestObjectivesData.Count() > 0)
            {
                foreach (var objective in this.AllObjectives)
                {
                    var latestData = latestObjectivesData.FirstOrDefault(obj => obj.ID == objective.ID && obj.Map == objective.Map);
                    if (latestData != null)
                    {
                        // Refresh owner information
                        if (objective.WorldOwner != latestData.WorldOwner)
                        {
                            Threading.InvokeOnUI(() =>
                            {
                                objective.PrevWorldOwner = objective.WorldOwner;
                                objective.WorldOwner = latestData.WorldOwner;

                                logger.Info("{0} - {1}: {2} -> {3}", objective.Map, objective.Name, objective.PrevWorldOwnerName, objective.WorldOwnerName);

                                // Bloodlust objectives don't get RI, so don't bother with a flip time or RI flag
                                if (objective.Type != ObjectiveType.TempleofLostPrayers
                                    && objective.Type != ObjectiveType.BattlesHollow
                                    && objective.Type != ObjectiveType.BauersEstate
                                    && objective.Type != ObjectiveType.OrchardOverlook
                                    && objective.Type != ObjectiveType.CarversAscent)
                                {
                                    objective.FlipTime = DateTime.UtcNow;
                                    objective.IsRIActive = true;
                                }
                            });

                            if (objective.WorldOwner != WorldColor.None) // Don't show a notification if the new owner is "none"
                            {
                                // Owner just changed, raise a notification!
                                this.DisplayNotification(objective);
                            }
                        }

                        // Refresh guild information
                        if (latestData.GuildOwner.HasValue)
                        {
                            if (!objective.GuildClaimer.ID.HasValue
                                || objective.GuildClaimer.ID.Value != latestData.GuildOwner.Value)
                            {
                                // Guild claimer has changed
                                Threading.InvokeOnUI(() => objective.GuildClaimer.ID = latestData.GuildOwner.Value);
                                var guildInfo = this.guildService.GetGuild(latestData.GuildOwner.Value);
                                if (guildInfo != null)
                                {
                                    Threading.InvokeOnUI(() =>
                                    {
                                        objective.GuildClaimer.Name = guildInfo.Name;
                                        objective.GuildClaimer.Tag = string.Format("[{0}]", guildInfo.Tag);
                                    });
                                }
                            }
                        }
                        else
                        {
                            Threading.InvokeOnUI(() =>
                            {
                                objective.GuildClaimer.ID = null;
                                objective.GuildClaimer.Name = string.Empty;
                                objective.GuildClaimer.Tag = string.Empty;
                            });
                        }
                    }
                    else
                    {
                        logger.Warn("Unable to retrieve latest data for {0} ({1})", objective.ID, objective.Name);
                    }
                }
            }
        }

        /// <summary>
        /// Refreshes all timer values, including RI
        /// </summary>
        private void RefreshTimers()
        {
            // Refresh timers
            foreach (var objective in this.AllObjectives)
            {
                var timeSinceFlip = DateTime.UtcNow - objective.FlipTime;
                if (timeSinceFlip <= TimeSpan.FromMinutes(5))
                {
                    var countdownTime = TimeSpan.FromMinutes(5) - timeSinceFlip;
                    Threading.InvokeOnUI(() => objective.TimerValue = countdownTime);
                }
                else
                {
                    Threading.InvokeOnUI(() => objective.IsRIActive = false);
                }
            }
        }

        /// <summary>
        /// Refreshes the score of all worlds
        /// </summary>
        private void RefreshWorldScores()
        {
            foreach (var team in this.Worlds)
            {
                var score = this.wvwService.GetWorldScore(team.WorldId);
                Threading.BeginInvokeOnUI(() => team.Score = score);
            }
        }

        /// <summary>
        /// Recalculates/refreshes all calculated distances
        /// </summary>
        private void CalculateDistances()
        {
            // Calculate time distances for all objectives, based on the player's position, if the player is in the same map as the objective
            // Note: these are approximations at best
            if (this.playerService.HasValidMapId)
            {
                var playerPosition = CalcUtil.ConvertToMapPosition(this.playerService.PlayerPosition);
                Threading.InvokeOnUI(() =>
                {
                    foreach (var objective in this.CurrentObjectives)
                    {
                        if (this.PlayerMap == objective.Map)
                        {
                            if (playerPosition != null && objective.ModelData.MapLocation != null)
                            {
                                objective.DistanceFromPlayer = Math.Round(CalcUtil.CalculateDistance(playerPosition, objective.ModelData.MapLocation, this.UserData.DistanceUnits));
                            }
                        }
                        else
                        {
                            objective.DistanceFromPlayer = 0;
                        }
                    }
                });
            }
        }

        /// <summary>
        /// Adds an objective to the notifications collection, and then removes the objective 10 seconds later
        /// </summary>
        private void DisplayNotification(WvWObjectiveViewModel objectiveData)
        {
            const int SLEEP_TIME = 250;

            if (this.CanShowNotification(objectiveData))
            {
                if (!this.WvWNotifications.Contains(objectiveData))
                {
                    Task.Factory.StartNew(() =>
                    {
                        logger.Info("Displaying notification for \"{0}\" in {1}", objectiveData.Name, objectiveData.Map);
                        Threading.BeginInvokeOnUI(() => this.WvWNotifications.Add(objectiveData));

                        if (this.UserData.NotificationDuration > 0)
                        {
                            // For X seconds, loop and sleep, with checks to see if notifications have been disabled
                            for (int i = 0; i < (this.UserData.NotificationDuration * 1000 / SLEEP_TIME); i++)
                            {
                                System.Threading.Thread.Sleep(SLEEP_TIME);
                                if (!this.CanShowNotification(objectiveData))
                                {
                                    logger.Debug("Removing notification for \"{0}\" in {1}", objectiveData.Name, objectiveData.Map);
                                    Threading.BeginInvokeOnUI(() => this.WvWNotifications.Remove(objectiveData));
                                }
                            }

                            logger.Debug("Removing notification for \"{0}\" in {1}", objectiveData.Name, objectiveData.Map);

                            // TODO: I hate having this here, but due to a limitation in WPF, there's no reasonable way around this at this time
                            // This makes it so that the notifications can fade out before they are removed from the notification window
                            Threading.BeginInvokeOnUI(() => objectiveData.IsRemovingNotification = true);
                            System.Threading.Thread.Sleep(SLEEP_TIME);
                            Threading.BeginInvokeOnUI(() =>
                            {
                                this.WvWNotifications.Remove(objectiveData);
                                objectiveData.IsRemovingNotification = false;
                            });
                        }
                    });
                }
            }
        }

        /// <summary>
        /// Determines if we can show a notification for the given objective, based on user settings
        /// </summary>
        /// <param name="objectiveData">The objective's data</param>
        /// <returns>True if the notification can be shown, else false</returns>
        private bool CanShowNotification(WvWObjectiveViewModel objectiveData)
        {
            bool canShow = false;

            if (this.UserData.AreNotificationsEnabled)
            {
                var homeTeam = this.Worlds.First(t => t.WorldId == this.UserData.WorldSelection.ID);

                if (this.UserData.NotifyWhenHomeTakesObjective
                    && objectiveData.WorldOwner == homeTeam.Color)
                {
                    canShow = true;
                }
                else if (this.UserData.NotifyWhenHomeLosesObjective
                    && objectiveData.PrevWorldOwner == homeTeam.Color)
                {
                    canShow = true;
                }
                else if (this.UserData.NotifyWhenOtherTakesOtherObjective
                    && objectiveData.PrevWorldOwner != homeTeam.Color
                    && objectiveData.WorldOwner != homeTeam.Color)
                {
                    canShow = true;
                }

                if (canShow)
                {
                    switch (objectiveData.Map)
                    {
                        case WvWMap.BlueBorderlands:
                            canShow = this.UserData.AreBlueBorderlandsNotificationsEnabled;
                            break;
                        case WvWMap.GreenBorderlands:
                            canShow = this.UserData.AreGreenBorderlandsNotificationsEnabled;
                            break;
                        case WvWMap.RedBorderlands:
                            canShow = this.UserData.AreRedBorderlandsNotificationsEnabled;
                            break;
                        case WvWMap.EternalBattlegrounds:
                            canShow = this.UserData.AreEternalBattlegroundsNotificationsEnabled;
                            break;
                        default:
                            canShow = false;
                            break;
                    }
                }

                if (canShow)
                {
                    switch (objectiveData.Type)
                    {
                        case ObjectiveType.Castle:
                            canShow = this.UserData.AreCastleNotificationsEnabled;
                            break;
                        case ObjectiveType.Keep:
                            canShow = this.UserData.AreKeepNotificationsEnabled;
                            break;
                        case ObjectiveType.Tower:
                            canShow = this.UserData.AreTowerNotificationsEnabled;
                            break;
                        case ObjectiveType.Camp:
                            canShow = this.UserData.AreCampNotificationsEnabled;
                            break;
                        case ObjectiveType.BattlesHollow:
                        case ObjectiveType.BauersEstate:
                        case ObjectiveType.CarversAscent:
                        case ObjectiveType.OrchardOverlook:
                        case ObjectiveType.TempleofLostPrayers:
                            canShow = this.UserData.AreBloodlustNotificationsEnabled;
                            break;
                        default:
                            canShow = false;
                            break;
                    }
                }
            }

            return canShow;
        }
    }
}
