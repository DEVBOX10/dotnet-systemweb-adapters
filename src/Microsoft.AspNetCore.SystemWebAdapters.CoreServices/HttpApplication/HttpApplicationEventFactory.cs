// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Web;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.SystemWebAdapters;

internal partial class HttpApplicationEventFactory
{
    private readonly HashSet<string> UnsupportedEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        // Fired before the ASP.NET page framework sends content to a requesting client (browser).
        "Application_PreSendContent",
        
        // Fired when the last instance of an HttpApplication class is destroyed. It's fired only once during an application's lifetime.
        "Application_End",
    };

    private readonly Dictionary<string, Action<HttpApplication, EventHandler>> KnownEvents = new(StringComparer.OrdinalIgnoreCase)
    {   
        // Fired just before an application is destroyed. This is the ideal location for cleaning up previously used resources.
        { "Application_Disposed", (app, handler) => app.Disposed += handler },

        // Fired when an unhandled exception is encountered within the application.
        { "Application_Error", (app, handler) => app.Error += handler },

        // Fired when an application request is received. It's the first event fired for a request, which is often a page request (URL) that a user enters.
        { "Application_BeginRequest", (app, handler) => app.BeginRequest += handler },

        // The last event fired for an application request.
        { "Application_EndRequest", (app, handler) => app.EndRequest += handler },

        // Fired before the ASP.NET page framework begins executing an event handler like a page or Web service.
        { "Application_PreRequestHandlerExecute", (app, handler) => app.PreRequestHandlerExecute += handler },

        // Fired when the ASP.NET page framework is finished executing an event handler.
        { "Application_PostRequestHandlerExecute", (app, handler) => app.PostRequestHandlerExecute += handler },

        // Fired before the ASP.NET page framework sends HTTP headers to a requesting client (browser).
        { "Application_PreSendRequestHeaders", (app, handler) => app.PreSendRequestHeaders += handler },


        // Fired when the ASP.NET page framework gets the current state (Session state) related to the current request.
        { "Application_AcquireRequestState", (app, handler) => app.AcquireRequestState += handler },

        // Fired when the ASP.NET page framework completes execution of all event handlers. This results in all state modules to save their current state data.
        { "Application_ReleaseRequestState", (app, handler) => app.ReleaseRequestState += handler },

        // Fired when the ASP.NET page framework completes an authorization request. It allows caching modules to serve the request from the cache, thus bypassing handler execution.
        { "Application_ResolveRequestCache", (app, handler) => app.ResolveRequestCache += handler },

        // Fired when the ASP.NET page framework completes handler execution to allow caching modules to store responses to be used to handle subsequent requests.
        { "Application_UpdateRequestCache", (app, handler) => app.UpdateRequestCache += handler },

        // Fired when the security module has established the current user's identity as valid. At this point, the user's credentials have been validated.
        { "Application_AuthenticateRequest", (app, handler) => app.AuthenticateRequest += handler },

        // Fired when the security module has verified that a user can access resources.
        { "Application_AuthorizeRequest", (app, handler) => app.AuthorizeRequest += handler },

        // Fired when a new user visits the application Web site.
        { "Session_Start", (app, handler) => app.SessionStart += handler },

        // Fired when a user's session times out, ends, or they leave the application Web site.
        { "Session_End", (app, handler) => app.SessionEnd += handler },
    };

    [LoggerMessage(0, LogLevel.Information, "Registered event {ApplicationType}.{EventName}")]
    private partial void LogRegistration(string applicationType, string eventName);

    [LoggerMessage(1, LogLevel.Warning, "HttpApplication event {ApplicationType}.{EventName} is unsupported")]
    private partial void LogUnsupported(string applicationType, string eventName);

    [LoggerMessage(2, LogLevel.Warning, "{ApplicationType}.{EventName} has unsupported signature")]
    private partial void LogInvalid(string applicationType, string eventName);

    private readonly ConcurrentDictionary<Type, EventManager> _applicationInitializers;
    private readonly ILogger<HttpApplicationEventFactory> _logger;

    public HttpApplicationEventFactory(ILogger<HttpApplicationEventFactory> logger)
    {
        _applicationInitializers = new();
        _logger = logger;
    }

    public void InitializeEvents(HttpApplication app)
    {
        var initializer = _applicationInitializers.GetOrAdd(app.GetType(), CreateEventManager, app);

        // This is invoked the first time an HttpApplication is constructed
        initializer.Application_Init?.Invoke(app)?.Invoke(app, EventArgs.Empty);

        initializer.Initialize?.Invoke(app);
    }

    private EventManager CreateEventManager(Type type, HttpApplication app)
    {
        var known = BuildEventManager(type);

        // This is invoked the first time an HttpApplication is constructed
        known.Application_Start?.Invoke(app)?.Invoke(app, EventArgs.Empty);

        return known;
    }

    private EventManager BuildEventManager(Type type)
    {
        var typeName = type.FullName ?? type.Name;
        var known = new EventManager();

        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            var state = EventParseState.None;

            if (UnsupportedEvents.Contains(method.Name))
            {
                state = EventParseState.NotSupported;
            }
            else if (KnownEvents.TryGetValue(method.Name, out var registration))
            {
                if (CreateHandler(method, ref state) is { } handler)
                {
                    known.Initialize += app => registration(app, handler(app));
                }
            }
            else
            {
                switch (method.Name)
                {
                    // Fired when an application initializes or is first called. It's invoked for all HttpApplication object instances.
                    case "Application_Init":
                        known.Application_Init = CreateHandler(method, ref state);
                        break;

                    // Fired when the first instance of the HttpApplication class is created. It allows you to create objects that are accessible by all HttpApplication instances.
                    case "Application_Start":
                        known.Application_Start = CreateHandler(method, ref state);
                        break;
                }
            }

            if (state is EventParseState.Registered)
            {
                LogRegistration(typeName, method.Name);
            }
            else if (state is EventParseState.NotSupported)
            {
                LogUnsupported(typeName, method.Name);
            }
            else if (state is EventParseState.InvalidSignature)
            {
                LogInvalid(typeName, method.Name);
            }
        }

        return known;
    }

    private static BindableEventHandler? CreateHandler(MethodInfo method, ref EventParseState state)
    {
        var parameters = method.GetParameters();

        if (method.ReturnType == typeof(void))
        {
            if (parameters.Length == 0)
            {
                state = EventParseState.Registered;

                return app =>
                {
                    var d = method.CreateDelegate<Action>(app);

                    return (s, e) => d();
                };
            }

            if (parameters.Length == 2 && parameters[0].ParameterType == typeof(object) && parameters[1].ParameterType == typeof(EventArgs))
            {
                state = EventParseState.Registered;
                return app => method.CreateDelegate<EventHandler>(app);
            }
        }

        state = EventParseState.InvalidSignature;
        return null;
    }

    private delegate EventHandler BindableEventHandler(HttpApplication app);

    private class EventManager
    {
        public BindableEventHandler? Application_Init { get; set; }

        public BindableEventHandler? Application_Start { get; set; }

        public Action<HttpApplication>? Initialize { get; set; }
    }

    private enum EventParseState
    {
        None,
        Registered,
        NotSupported,
        InvalidSignature,
    }
}
