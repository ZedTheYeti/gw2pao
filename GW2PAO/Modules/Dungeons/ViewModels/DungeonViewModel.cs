﻿using GW2PAO.API.Data;
using GW2PAO.API.Data.Entities;
using GW2PAO.API.Services;
using GW2PAO.Data;
using GW2PAO.Data.UserData;
using GW2PAO.Modules.WebBrowser.Interfaces;
using GW2PAO.PresentationCore;
using Microsoft.Practices.Prism.Mvvm;
using NLog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GW2PAO.Modules.Dungeons.Data;
using GW2PAO.Modules.Dungeons.Interfaces;

namespace GW2PAO.Modules.Dungeons.ViewModels
{
    public class DungeonViewModel : BindableBase
    {
        /// <summary>
        /// Default logger
        /// </summary>
        private static Logger logger = LogManager.GetCurrentClassLogger();

        private DungeonsUserData userData;
        private bool isVisible;
        private bool isActiveDungeon;

        /// <summary>
        /// The primary model object containing the dungeon information
        /// </summary>
        public GW2PAO.API.Data.Entities.Dungeon DungeonModel { get; private set; }

        /// <summary>
        /// The dungeons's ID
        /// </summary>
        public Guid DungeonId { get { return this.DungeonModel.ID; } }

        /// <summary>
        /// The dungeons's name
        /// </summary>
        public string DungeonName { get { return this.DungeonModel.Name; } }

        /// <summary>
        /// Name of the zone in which the dungeon is located
        /// </summary>
        public string ZoneName { get { return "Located in " + this.DungeonModel.MapName; } }

        /// <summary>
        /// Minimum level requirement for the dungeon
        /// </summary>
        public string MinimumLevel { get { return "Minimum Level: " + this.DungeonModel.MinimumLevel; } }

        /// <summary>
        /// Command to hide the dungeon
        /// </summary>
        public DelegateCommand HideCommand { get { return new DelegateCommand(this.AddToHiddenDungeons); } }

        /// <summary>
        /// Command to copy the nearest waypoint's chat code to the clipboard
        /// </summary>
        public DelegateCommand CopyWaypointCommand { get { return new DelegateCommand(this.CopyWaypointCode); } }

        /// <summary>
        /// Visibility of the dungeon
        /// Visibility is based on multiple properties, including:
        ///     - Minimum Level and Character's Level, and the user configuration for if unreachable dungeons are shown
        ///     - Whether or not the dungeon is user-configured as hidden
        /// </summary>
        public bool IsVisible
        {
            get { return this.isVisible; }
            set { SetProperty(ref this.isVisible, value); }
        }

        /// <summary>
        /// True if the dungeon is currently being completed by the player
        /// </summary>
        public bool IsActive
        {
            get { return this.isActiveDungeon; }
            set { this.SetProperty(ref this.isActiveDungeon, value); }
        }

        /// <summary>
        /// Collection of dungeon paths for this dungeon
        /// </summary>
        public ObservableCollection<PathViewModel> Paths { get; private set; }

        /// <summary>
        /// Default constructor
        /// </summary>
        /// <param name="dungeon">The dungeon information</param>
        /// <param name="dungeonsController">The dungeons controller</param>
        /// <param name="browserController">The browser controller object for displaying wiki information</param>
        /// <param name="userData">The dungeon user settings</param>
        public DungeonViewModel(GW2PAO.API.Data.Entities.Dungeon dungeon, IDungeonsController dungeonsController, IWebBrowserController browserController, DungeonsUserData userData)
        {
            this.DungeonModel = dungeon;
            this.userData = userData;

            // Initialize the path view models
            this.Paths = new ObservableCollection<PathViewModel>();
            foreach (var path in this.DungeonModel.Paths)
            {
                this.Paths.Add(new PathViewModel(path, dungeonsController, browserController, this.userData));
            }

            this.RefreshVisibility();
            this.userData.PropertyChanged += (o, e) => this.RefreshVisibility();
            this.userData.HiddenDungeons.CollectionChanged += (o, e) => this.RefreshVisibility();
        }

        /// <summary>
        /// Adds the dungeon to the list of hidden dungeons in the user settings
        /// </summary>
        private void AddToHiddenDungeons()
        {
            logger.Debug("Adding \"{0}\" to hidden dungeons", this.DungeonName);
            this.userData.HiddenDungeons.Add(this.DungeonId);
        }

        /// <summary>
        /// Copies the nearest waypoint's chat code to the clipboard
        /// </summary>
        private void CopyWaypointCode()
        {
            logger.Debug("Copying waypoint code of \"{0}\" as \"{1}\"", this.DungeonName, this.DungeonModel.WaypointCode);
            System.Windows.Clipboard.SetDataObject(this.DungeonModel.WaypointCode);
        }

        /// <summary>
        /// Refreshes the visibility of the event
        /// </summary>
        private void RefreshVisibility()
        {
            logger.Trace("Refreshing visibility of \"{0}\"", this.DungeonName);
            if (this.userData.HiddenDungeons.Any(id => id == this.DungeonId))
            {
                this.IsVisible = false;
            }
            else
            {
                this.IsVisible = true;
            }
            logger.Trace("IsVisible = {0}", this.IsVisible);
        }
    }
}
