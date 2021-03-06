﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GW2PAO.API.Data;
using GW2PAO.Modules.Events.ViewModels;

namespace GW2PAO.Modules.Events.Interfaces
{
    public interface IEventsController
    {
        /// <summary>
        /// The collection of World Events
        /// </summary>
        ObservableCollection<EventViewModel> WorldEvents { get; }

        /// <summary>
        /// The collection of events for event notifications
        /// </summary>
        ObservableCollection<EventViewModel> EventNotifications { get; }

        /// <summary>
        /// The interval by which to refresh events (in ms)
        /// </summary>
        int EventRefreshInterval { get; set; }

        /// <summary>
        /// The event tracker user data
        /// </summary>
        EventsUserData UserData { get; }

        /// <summary>
        /// Starts the controller
        /// </summary>
        void Start();

        /// <summary>
        /// Stops the controller
        /// </summary>
        void Stop();

        /// <summary>
        /// Forces a shutdown of the controller, including all running timers/threads
        /// </summary>
        void Shutdown();
    }
}
