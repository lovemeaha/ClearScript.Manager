﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClearScript.Manager.Caching;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;

namespace ClearScript.Manager
{
    /// <summary>
    /// Runtime Manager used to execute scripts within a runtime.
    /// </summary>
    public interface IRuntimeManager
    {
        /// <summary>
        /// If True, automatically adds a reference to the .Net Console.
        /// </summary>
        bool AddConsoleReference { get; set; }

        /// <summary>
        /// Executes the provided script.
        /// </summary>
        /// <param name="scriptId">Id of the script for caching purposes.</param>
        /// <param name="code">Script text to execute.</param>
        /// <param name="addToCache">Indicates that this script should be added to the script cache once compiled.  Default is True.</param>
        /// <returns>Task to await.</returns>
        Task ExecuteAsync(string scriptId, string code, bool addToCache = true);

        /// <summary>
        /// Executes the provided script.
        /// </summary>
        /// <param name="scriptId">Id of the script for caching purposes.</param>
        /// <param name="code">Script text to execute.</param>
        /// <param name="configAction">An action that accepts the V8 script engine before it's use.</param>
        /// <param name="addToCache">Indicates that this script should be added to the script cache once compiled.  Default is True.</param>
        /// <returns>Task to await.</returns>
        Task ExecuteAsync(string scriptId, string code, Action<V8ScriptEngine> configAction, bool addToCache = true);

        /// <summary>
        /// Executes the provided script.
        /// </summary>
        /// <param name="scriptId">Id of the script for caching purposes.</param>
        /// <param name="code">Script text to execute.</param>
        /// <param name="addToCache">Indicates that this script should be added to the script cache once compiled.  Default is True.</param>
        /// <param name="hostObjects">Objects to inject into the JavaScript runtime.</param>
        /// <param name="hostTypes">Types to make available to the JavaScript runtime.</param>
        /// <returns>Task to await.</returns>
        Task ExecuteAsync(string scriptId, string code, IList<HostObject> hostObjects, IList<HostType> hostTypes, bool addToCache = true);

        /// <summary>
        /// Compiles the provided script.
        /// </summary>
        /// <param name="scriptId">Id of the script for caching purposes.</param>
        /// <param name="code">Script text to execute.</param>
        /// <param name="addToCache">Indicates that this script should be added to the script cache once compiled.  Default is True.</param>
        /// <param name="cacheExpirationSeconds">Number of seconds that the V8 Script will be cached.</param>
        /// <returns>Compiled V8 script.</returns>
        V8Script Compile(string scriptId, string code, bool addToCache = true, int? cacheExpirationSeconds = null);

        /// <summary>
        /// Attempts to get the cached script by Id.
        /// </summary>
        /// <param name="scriptId">Id to search on.</param>
        /// <param name="script">Cached script (output)</param>
        /// <returns>Bool indicating that cached script was located.</returns>
        bool TryGetCached(string scriptId, out CachedV8Script script);
    }


    /// <summary>
    /// Runtime Manager used to execute scripts within a runtime.
    /// </summary>
    public class RuntimeManager : IRuntimeManager
    {
        private readonly IManagerSettings _settings;
        private readonly LruCache<string, CachedV8Script> _scriptCache;

        private readonly V8Runtime _v8Runtime;

        /// <summary>
        /// Creates a new Runtime Manager.
        /// </summary>
        /// <param name="settings">Settings to apply to the Runtime Manager and the scripts it runs.</param>
        public RuntimeManager(IManagerSettings settings)
        {
            _settings = settings;
            _scriptCache = new LruCache<string, CachedV8Script>(LurchTableOrder.Access, settings.ScriptCacheMaxCount);

            _v8Runtime = new V8Runtime(new V8RuntimeConstraints
            {
                MaxExecutableSize = settings.MaxExecutableBytes,
                MaxOldSpaceSize = settings.MaxOldSpaceBytes,
                MaxNewSpaceSize = settings.MaxNewSpaceBytes
            });
        }

        public bool AddConsoleReference { get; set; }

        public async Task ExecuteAsync(string scriptId, string code, bool addToCache = true)
        {
            await ExecuteAsync(scriptId, code, null, addToCache);
        }

        public async Task ExecuteAsync(string scriptId, string code, Action<V8ScriptEngine> configAction, bool addToCache = true)
        {
            V8Script compiledScript = Compile(scriptId, code, addToCache);

            try
            {
                using (V8ScriptEngine engine = _v8Runtime.CreateScriptEngine(V8ScriptEngineFlags.DisableGlobalMembers))
                {
                    if (AddConsoleReference)
                    {
                        engine.AddHostType("Console", typeof (Console));
                    }

                    if (configAction != null)
                    {
                        configAction(engine);
                    }
                    
                    var cancellationToken = CreateCancellationToken(engine);

                    // ReSharper disable once AccessToDisposedClosure
                    await Task.Run(() => engine.Evaluate(compiledScript), cancellationToken);
                }
            }
            catch (ScriptInterruptedException ex)
            {
                var newEx = new ScriptInterruptedException("Script interruption occurred, this often indicates a script timeout.  Examine the data and inner exception for more information.", ex);
                newEx.Data.Add("Timeout", _settings.ScriptTimeoutMilliSeconds);
                newEx.Data.Add("ScriptId", scriptId);

                throw newEx;
            }
        }

        public async Task ExecuteAsync(string scriptId, string code, IList<HostObject> hostObjects, IList<HostType> hostTypes, bool addToCache = true)
        {

            var configAction = new Action<V8ScriptEngine>(engine =>
                                    {
                                        if (hostObjects != null)
                                        {
                                            foreach (HostObject hostObject in hostObjects)
                                            {
                                                engine.AddHostObject(hostObject.Name, hostObject.Flags, hostObject.Target);
                                            }
                                        }

                                        if (hostTypes != null)
                                        {
                                            foreach (HostType hostType in hostTypes)
                                            {
                                                if (hostType.Type != null)
                                                {
                                                    engine.AddHostType(hostType.Name, hostType.Type);
                                                }
                                                else if (hostType.HostTypeCollection != null)
                                                {
                                                    engine.AddHostType(hostType.Name, hostType.Type);
                                                }
                                            }
                                        }
                                    });

            await ExecuteAsync(scriptId, code, configAction, addToCache);

        }

        private CancellationToken CreateCancellationToken(V8ScriptEngine engine)
        {
            var cancellationSource = new CancellationTokenSource(_settings.ScriptTimeoutMilliSeconds);
            var cancellationToken = cancellationSource.Token;
            cancellationToken.ThrowIfCancellationRequested();
            cancellationToken.Register(engine.Interrupt);
            return cancellationToken;
        }

        public V8Script Compile(string scriptId, string code, bool addToCache = true, int? cacheExpirationSeconds = null)
        {
            CachedV8Script cachedScript;
            if (TryGetCached(scriptId, out cachedScript))
            {
                return cachedScript.Script;
            }

            V8Script compiledScript = _v8Runtime.Compile(scriptId, code);

            if (addToCache)
            {
                if (!cacheExpirationSeconds.HasValue)
                {
                    cacheExpirationSeconds = _settings.ScriptCacheExpirationSeconds;
                }
                if (cacheExpirationSeconds > 0)
                {
                    var cacheEntry = new CachedV8Script(compiledScript, cacheExpirationSeconds.Value);
                    _scriptCache.AddOrUpdate(scriptId, cacheEntry, (key, original) => cacheEntry);
                }
            }

            return compiledScript;
        }

        public bool TryGetCached(string scriptId, out CachedV8Script cachedScript)
        {
            if (_scriptCache.TryGetValue(scriptId, out cachedScript))
            {
                if (cachedScript.ExpiresOn > DateTime.UtcNow)
                {
                    cachedScript.CacheHits++;
                    return true;
                }
                _scriptCache.TryRemove(scriptId, out cachedScript);
            }
            cachedScript = null;
            return false;
        }
    }
}