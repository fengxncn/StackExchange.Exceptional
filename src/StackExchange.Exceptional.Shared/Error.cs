﻿using Newtonsoft.Json;
using StackExchange.Exceptional.Internal;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace StackExchange.Exceptional;

/// <summary>
/// Represents a logical application error (as opposed to the actual <see cref="Exception"/> it may be representing).
/// </summary>
[Serializable]
public class Error
{
    /// <summary>
    /// The ID on this error, strictly for primary keying on persistent stores.
    /// </summary>
    [JsonIgnore]
    public long Id { get; set; }

    /// <summary>
    /// Unique identifier for this error, generated on the server it came from.
    /// </summary>
    public Guid GUID { get; set; }

    /// <summary>
    /// Settings used to log this error.
    /// Note: THIS IS LIKELY THE GLOBAL INSTANCE, so changes here likely impact all exceptions.
    /// </summary>
    [JsonIgnore]
    public ExceptionalSettingsBase Settings { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="Error"/> class.
    /// </summary>
    public Error() { }

    /// <summary>
    /// Initializes a new instance of the <see cref="Error"/> class from a given <see cref="Exception"/> instance.
    /// </summary>
    /// <param name="e">The exception we intend to log.</param>
    /// <param name="settings">The settings this error is being logged with.</param>
    /// <param name="category">The category to associate with this exception.</param>
    /// <param name="applicationName">The application name to log as (used for overriding current settings).</param>
    /// <param name="rollupPerServer">Whether to log up per-server, e.g. errors are only duplicates if they have same stack on the same machine.</param>
    /// <param name="initialCustomData">The initial data dictionary to start with (generated by the user).</param>
    public Error(Exception e,
        ExceptionalSettingsBase settings,
        string category = null,
        string applicationName = null,
        bool rollupPerServer = false,
        Dictionary<string, string> initialCustomData = null)
    {
        Exception = e ?? throw new ArgumentNullException(nameof(e));
        Settings = settings;
        var baseException = e;

        // If it's not a .NET framework exception, usually more information is being added,
        // so use the wrapper for the message, type, etc.
        // If it's a .NET framework exception type, drill down and get the innermost exception.
        if (e.IsBCLException())
            baseException = e.GetBaseException();

        GUID = Guid.NewGuid();
        ApplicationName = applicationName ?? settings.DefaultStoreIfExists?.ApplicationName ?? settings.Store?.ApplicationName;
        Category = category;
        MachineName = Environment.MachineName;
        Type = baseException.GetType().FullName;
        Message = baseException.Message;
        Source = baseException.Source;
        Detail = e.ToString();
        CreationDate = DateTime.UtcNow;
        DuplicateCount = 1;
        CustomData = initialCustomData;

        // Calculate the StackTrace if needed
        // TODO: Move this to generate Detail once instead of using .ToString()
        // Mirror CoreFX code for now, and then move to SourceLink supporting code after
        if (Settings.AppendFullStackTraces)
        {
            Detail += "\n\nFull Trace:\n\n" + new StackTrace(3, true);
        }

        ErrorHash = GetHash(rollupPerServer);

        var exCursor = e;
        while (exCursor != null)
        {
            AddData(exCursor);
            exCursor = exCursor.InnerException;
        }
        AddCustomData();
    }

    /// <summary>
    /// Only allocate this dictionary if there's a need.
    /// </summary>
    private void InitCustomData() => CustomData ??= [];

    /// <summary>
    /// Adds data from the .Data on an Exception. Note: runs for every primary and inner exception
    /// </summary>
    /// <param name="exception">The current exception we're looping over.</param>
    private void AddData(Exception exception)
    {
        ProcessHandlers(exception);

        // Regardless of what Resharper may be telling you, .Data can be null on things like a null ref exception.
        if (exception.Data == null) return;

        if (exception.Data.Keys.Count > 0)
        {
            var regex = Settings.DataIncludeRegex;

            foreach (string k in exception.Data.Keys)
            {
                if (regex?.IsMatch(k) == true)
                {
                    InitCustomData();
                    CustomData[k] = exception.Data[k] != null ? exception.Data[k].ToString() : string.Empty;
                }
                else if (k.StartsWith(Constants.CustomDataKeyPrefix) && k.Length > Constants.CustomDataKeyPrefix.Length)
                {
                    InitCustomData();
                    CustomData[k[Constants.CustomDataKeyPrefix.Length..]] = exception.Data[k]?.ToString();
                }
            }
        }
    }

    private void ProcessHandlers(Exception exception)
    {
        // TODO: Potentially loop through all base types as well when seeking handlers
        // For example, should a handler of "System.Exception" always run?
        // If so, the bottom special case could be moved to a default handler on "System.Exception"
        var handlers = Settings.ExceptionActions;
        if (handlers.TryGetValue(exception.GetType().FullName, out var handler))
        {
            handler(this);
        }

        // Run handlers for any exception implementing IExceptionalHandled
        if (exception is IExceptionalHandled eh)
        {
            try
            {
                eh.ExceptionalHandler(this);
            }
            catch (Exception ehe)
            {
                Trace.WriteLine(ehe);
            }
        }
    }

    /// <summary>
    /// Adds a command to log on this error.
    /// </summary>
    /// <param name="command">The command to add.</param>
    /// <returns>The added command.</returns>
    public Command AddCommand(Command command)
    {
        (Commands ??= []).Add(command);
        return command;
    }

    /// <summary>
    /// Populates the CustomData collection via <see cref="ExceptionalSettingsBase.GetCustomData"/>, if set.
    /// </summary>
    private void AddCustomData()
    {
        if (Settings.GetCustomData != null)
        {
            InitCustomData();
            try
            {
                Settings.GetCustomData(Exception, CustomData);
            }
            catch (Exception cde)
            {
                // if there was an error getting custom errors, log it so we can display such in the view...and not fail to log the original error
                CustomData.Add(Constants.CustomDataErrorKey, cde.ToString());
            }
        }
    }

    /// <summary>
    /// Logs this error to a specific store.
    /// </summary>
    /// <param name="store">The store to log to, is null the default is used.</param>
    /// <returns>The error if logged, or null if logging was aborted.</returns>
    public bool LogToStore(ErrorStore store = null)
    {
        store ??= Settings.DefaultStore;
        var abort = Settings.BeforeLog(this, store);
        if (abort) return false; // if we've been told to abort, then abort dammit!

        Trace.WriteLine(Exception); // always echo the error to trace for local debugging
        store.Log(this);

        Settings.AfterLog(this, store);

        return true;
    }

    /// <summary>
    /// Logs this error to a specific store.
    /// </summary>
    /// <param name="store">The store to log to, if null the default is used.</param>
    /// <returns>The error if logged, or null if logging was aborted.</returns>
    public async Task<bool> LogToStoreAsync(ErrorStore store = null)
    {
        store ??= Settings.DefaultStore;
        var abort = Settings.BeforeLog(this, store);
        if (abort) return true; // if we've been told to abort, then abort dammit!

        Trace.WriteLine(Exception); // always echo the error to trace for local debugging
        await store.LogAsync(this).ConfigureAwait(false);

        Settings.AfterLog(this, store);

        return true;
    }

    /// <summary>
    /// Gets a unique-enough hash of this error. Stored as a quick comparison mechanism to roll-up duplicate errors.
    /// </summary>
    /// <param name="includeMachine">Whether to include <see cref="MachineName"/> in the has calculation, creating per-machine roll-ups.</param>
    /// <returns>A "Unique" hash for this error.</returns>
    public int? GetHash(bool includeMachine)
    {
        if (!Detail.HasValue()) return null;

        var result = Detail.GetHashCode();
        if (includeMachine && MachineName.HasValue())
            result = (result * 397) ^ MachineName.GetHashCode();

        return result;
    }

    /// <summary>
    /// Whether this error is protected from deletion.
    /// </summary>
    public bool IsProtected { get; set; }

    /// <summary>
    /// For notifier usage - whether this error is a duplicate (already seen recently).
    /// Recent is defined by the <see cref="ErrorStoreSettings.RollupPeriod"/> setting.
    /// </summary>
    [JsonIgnore]
    public bool IsDuplicate { get; set; }

    /// <summary>
    /// The <see cref="Exception"/> instance used to create this error.
    /// </summary>
    [JsonIgnore]
    public Exception Exception { get; set; }

    /// <summary>
    /// The name of the application that threw this exception.
    /// </summary>
    public string ApplicationName { get; set; }

    /// <summary>
    /// The category of this error, usage is up to the user.
    /// It could be a tag, or severity, etc.
    /// </summary>
    public string Category { get; set; }

    /// <summary>
    /// The hostname where the exception occurred.
    /// </summary>
    public string MachineName { get; set; }

    /// <summary>
    /// The type error.
    /// </summary>
    public string Type { get; set; }

    /// <summary>
    /// The source of this error.
    /// </summary>
    public string Source { get; set; }

    /// <summary>
    /// Exception message.
    /// </summary>
    public string Message { get; set; }

    /// <summary>
    /// The detail/stack trace of this error.
    /// </summary>
    public string Detail { get; set; }

    /// <summary>
    /// The hash that describes this error.
    /// </summary>
    public int? ErrorHash { get; set; }

    /// <summary>
    /// The time in UTC that the error originally occurred.
    /// </summary>
    public DateTime CreationDate { get; set; }

    /// <summary>
    /// The time in UTC that the error last occurred.
    /// </summary>
    public DateTime? LastLogDate { get; set; }

    /// <summary>
    /// The HTTP Status code associated with the request.
    /// </summary>
    public int? StatusCode { get; set; }

    /// <summary>
    /// The server variables collection for the request.
    /// </summary>
    [JsonIgnore]
    public NameValueCollection ServerVariables { get; set; }

    /// <summary>
    /// The query string collection for the request.
    /// </summary>
    [JsonIgnore]
    public NameValueCollection QueryString { get; set; }

    /// <summary>
    /// The form collection for the request.
    /// </summary>
    [JsonIgnore]
    public NameValueCollection Form { get; set; }

    /// <summary>
    /// A collection representing the client cookies of the request.
    /// </summary>
    [JsonIgnore]
    public NameValueCollection Cookies { get; set; }

    /// <summary>
    /// A collection representing the headers sent with the request.
    /// </summary>
    [JsonIgnore]
    public NameValueCollection RequestHeaders { get; set; }

    /// <summary>
    /// A collection of custom data added at log time.
    /// </summary>
    public Dictionary<string, string> CustomData { get; set; }

    /// <summary>
    /// The number of newer Errors that have been discarded because they match this Error and fall
    /// within the configured <see cref="ErrorStoreSettings.RollupPeriod"/> <see cref="TimeSpan"/> value.
    /// </summary>
    public int? DuplicateCount { get; set; }

    /// <summary>
    /// The commands associated with this error. For example: SQL queries, Redis commands, elastic queries, etc.
    /// </summary>
    public List<Command> Commands { get; set; }

    /// <summary>
    /// Date this error was deleted (for stores that support deletion and retention, e.g. SQL)
    /// </summary>
    public DateTime? DeletionDate { get; set; }

    /// <summary>
    /// The URL host of the request causing this error.
    /// </summary>
    public string Host { get; set; }

    /// <summary>
    /// The URL *path* of the request causing this error, e.g. /MyContoller/MyAction
    /// </summary>
    [JsonProperty(nameof(Url))] // Legacy compatibility
    public string UrlPath { get; set; }

    /// <summary>
    /// The complete URL of the request causing this error.
    /// </summary>
    public string FullUrl { get; set; }

    /// <summary>
    /// The HTTP Method causing this error, e.g. GET or POST.
    /// </summary>
    public string HTTPMethod { get; set; }

    /// <summary>
    /// The IPAddress of the request causing this error.
    /// </summary>
    public string IPAddress { get; set; }

    /// <summary>
    /// JSON populated from database stored, deserialized after if needed.
    /// </summary>
    [JsonIgnore]
    public string FullJson { get; set; }

    /// <summary>
    /// Returns the value of the <see cref="Message"/> property.
    /// </summary>
    public override string ToString() => Message;

    /// <summary>
    /// Gets the full URL associated with the request that threw this error.
    /// </summary>
    /// <remarks>
    /// Accounts for HTTPS from load balancers via X-Forwarded-Proto.
    /// </remarks>
    /// <returns>The full URL, if it can be determined, an empty string otherwise.</returns>
    public string GetFullUrl()
    {
        if (FullUrl.IsNullOrEmpty())
        {
            return string.Empty;
        }
        if (RequestHeaders?[KnownHeaders.XForwardedProto]?.StartsWith("https") == true && FullUrl.StartsWith("http://"))
        {
            return "https://" + FullUrl["http://".Length..];
        }
        return FullUrl;
    }

    /// <summary>
    /// Create a copy of the error and collections so if it's modified in memory logging is not affected.
    /// </summary>
    /// <returns>A clone of this error.</returns>
    public Error Clone()
    {
        var copy = (Error) MemberwiseClone();
        if (ServerVariables != null) copy.ServerVariables = new NameValueCollection(ServerVariables);
        if (QueryString != null) copy.QueryString = new NameValueCollection(QueryString);
        if (Form != null) copy.Form = new NameValueCollection(Form);
        if (Cookies != null) copy.Cookies = new NameValueCollection(Cookies);
        if (RequestHeaders != null) copy.RequestHeaders = new NameValueCollection(RequestHeaders);
        if (CustomData != null) copy.CustomData = new Dictionary<string, string>(CustomData);
        return copy;
    }

    /// <summary>
    /// Variables strictly for JSON serialization, to maintain non-dictionary behavior.
    /// </summary>
    public List<NameValuePair> ServerVariablesSerializable
    {
        get => GetPairs(ServerVariables);
        set => ServerVariables = GetNameValueCollection(value);
    }
    /// <summary>
    /// Variables strictly for JSON serialization, to maintain non-dictionary behavior.
    /// </summary>
    public List<NameValuePair> QueryStringSerializable
    {
        get => GetPairs(QueryString);
        set => QueryString = GetNameValueCollection(value);
    }
    /// <summary>
    /// Variables strictly for JSON serialization, to maintain non-dictionary behavior.
    /// </summary>
    public List<NameValuePair> FormSerializable
    {
        get => GetPairs(Form);
        set => Form = GetNameValueCollection(value);
    }
    /// <summary>
    /// Variables strictly for JSON serialization, to maintain non-dictionary behavior.
    /// </summary>
    public List<NameValuePair> CookiesSerializable
    {
        get => GetPairs(Cookies);
        set => Cookies = GetNameValueCollection(value);
    }
    /// <summary>
    /// Variables strictly for JSON serialization, to maintain non-dictionary behavior.
    /// </summary>
    public List<NameValuePair> RequestHeadersSerializable
    {
        get => GetPairs(RequestHeaders);
        set => RequestHeaders = GetNameValueCollection(value);
    }

    /// <summary>
    /// Gets a JSON representation for this error.
    /// </summary>
    public string ToJson() => JsonConvert.SerializeObject(this);

    /// <summary>
    /// Gets a JSON representation for this error suitable for cross-domain.
    /// </summary>
    /// <param name="sb">The <see cref="StringBuilder"/> to write to.</param>
    public void WriteDetailedJson(StringBuilder sb)
    {
        using var sw = new StringWriter(sb);
        using var w = new JsonTextWriter(sw);
        w.StringEscapeHandling = StringEscapeHandling.EscapeHtml;
        JsonTextWriter WriteName(string name)
        {
            w.WritePropertyName(name);
            return w;
        }
        JsonTextWriter WritePairs(string name, List<NameValuePair> pairs)
        {
            WriteName(name).WriteStartObject();
            if (pairs != null)
            {
                foreach (var p in pairs)
                {
                    WriteName(p.Name).WriteValue(p.Value);
                }
            }
            w.WriteEndObject();
            return w;
        }
        JsonTextWriter WriteDictionary(string name, Dictionary<string, string> pairs)
        {
            WriteName(name).WriteStartObject();
            if (pairs != null)
            {
                foreach (var p in pairs)
                {
                    WriteName(p.Key).WriteValue(p.Value);
                }
            }
            w.WriteEndObject();
            return w;
        }
        w.WriteStartObject();
        WriteName(nameof(GUID)).WriteValue(GUID);
        WriteName(nameof(ApplicationName)).WriteValue(ApplicationName);
        WriteName(nameof(Category)).WriteValue(Category);
        WriteName(nameof(CreationDate)).WriteValue(CreationDate.ToEpochTime());
        WriteName(nameof(DeletionDate)).WriteValue(DeletionDate.ToEpochTime());
        WriteName(nameof(Detail)).WriteValue(Detail);
        WriteName(nameof(DuplicateCount)).WriteValue(DuplicateCount);
        WriteName(nameof(ErrorHash)).WriteValue(ErrorHash);
        WriteName(nameof(HTTPMethod)).WriteValue(HTTPMethod);
        WriteName(nameof(Host)).WriteValue(Host);
        WriteName(nameof(IPAddress)).WriteValue(IPAddress);
        WriteName(nameof(IsProtected)).WriteValue(IsProtected);
        WriteName(nameof(MachineName)).WriteValue(MachineName);
        WriteName(nameof(Message)).WriteValue(Message);
        WriteName(nameof(Source)).WriteValue(Source);
        WriteName(nameof(StatusCode)).WriteValue(StatusCode);
        WriteName(nameof(Type)).WriteValue(Type);
        WriteName("Url").WriteValue(UrlPath); // Legacy
        WriteName(nameof(FullUrl)).WriteValue(FullUrl);
        WriteName(nameof(QueryString)).WriteValue(ServerVariables?["QUERY_STRING"]);
        WriteDictionary(nameof(CustomData), CustomData);
        if (Commands != null)
        {
            foreach (var c in Commands)
            {
                WriteName(nameof(Commands)).WriteStartObject();
                WriteName(nameof(c.Type)).WriteValue(c.Type);
                WriteName(nameof(c.CommandString)).WriteValue(c.CommandString);
                WriteDictionary(nameof(c.Data), c.Data);
                w.WriteEndObject();
            }
        }
        WritePairs(nameof(ServerVariables), ServerVariablesSerializable);
        WritePairs(nameof(Cookies), CookiesSerializable);
        WritePairs(nameof(RequestHeaders), RequestHeadersSerializable);
        WritePairs(nameof(QueryString), QueryStringSerializable);
        WritePairs(nameof(Form), FormSerializable);
        w.WriteEndObject();
    }

    /// <summary>
    /// Deserializes provided JSON into an Error object.
    /// </summary>
    /// <param name="json">JSON representing an Error.</param>
    /// <returns>The Error object.</returns>
    public static Error FromJson(string json) => JsonConvert.DeserializeObject<Error>(json);

    /// <summary>
    /// Serialization class in place of the NameValueCollection pairs.
    /// </summary>
    /// <remarks>This exists because things like a querystring can halve multiple values, they are not a dictionary.</remarks>
    public class NameValuePair
    {
        /// <summary>
        /// The name for this variable.
        /// </summary>
        public string Name { get; set; }
        /// <summary>
        /// The value for this variable.
        /// </summary>
        public string Value { get; set; }
    }

    private static List<NameValuePair> GetPairs(NameValueCollection nvc)
    {
        var result = new List<NameValuePair>();
        if (nvc == null) return null;

        for (int i = 0; i < nvc.Count; i++)
        {
            result.Add(new NameValuePair {Name = nvc.GetKey(i), Value = nvc.Get(i)});
        }
        return result;
    }

    private static NameValueCollection GetNameValueCollection(List<NameValuePair> pairs)
    {
        var result = new NameValueCollection();
        if (pairs == null) return null;

        foreach(var p in pairs)
        {
            result.Add(p.Name, p.Value);
        }
        return result;
    }

    /// <summary>
    /// Legacy: Sets the SQL command text associated with this error.
    /// Strictly for deserialization of old errors.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [Obsolete("Only for deserializing old Errors, use .Commands now.")]
    public string SQL
    {
        set
        {
            if (value.HasValue())
            {
                AddCommand(new Command("SQL Server Query", value));
            }
        }
    }

    /// <summary>
    /// Legacy: Sets the <see cref="UrlPath"/> from older <see cref="Url"/> columns.
    /// Strictly for deserialization of old errors.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [Obsolete("Only for deserializing old Errors, use .UrlPath now.")]
    [JsonIgnore]
    public string Url
    {
        set => UrlPath = value;
    }
}
