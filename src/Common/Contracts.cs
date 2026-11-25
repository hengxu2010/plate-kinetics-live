// What the shared host (window, local web server) needs from a program's data side. The BMG program's
// Watcher (port of BMG/runs.py) and the Archimed program's Source (port of Archimed/archimed.py) implement it.
// The three data methods answer the browser exactly as the Python watcher's state(), run_data(key, id) and
// recorded() did: the returned values are serialised with Json.Serialize(value, ensureAscii: false) and must
// therefore be JSON values (see Json.cs: Dictionary<string, object>, List<object>, string, long, double, bool,
// null) without NaN or infinities. They are called from the web server's threads while the watcher runs its
// own thread: they must be thread-safe, as in Python.
using System;
using System.Collections.Generic;

namespace SAAControl
{
    public interface IWatcher
    {
        /// <summary>Begin watching (start the background thread). Called once.</summary>
        void Start();

        /// <summary>Stop watching and write what has to be written; returns when the thread has ended.</summary>
        void Stop();

        /// <summary>Python: watcher.state() (answer to GET /api/state).</summary>
        object State();

        /// <summary>Python: watcher.run_data(key, id) (GET /api/run?key=..&amp;id=..). null: not found (404).</summary>
        object RunData(string key, string id);

        /// <summary>Python: watcher.recorded() (GET /api/runs).</summary>
        object Recorded();

        /// <summary>The program's experiment files (data folder\Experiments: .pk and .xlsx of every run); null: none.</summary>
        Experiments Experiments { get; }

        /// <summary>The program's own routes (BMG: /api/bmg/history, /api/bmg/run, /api/bmg/save). method: "GET" or
        /// "POST". null: not one of its routes, or not found (404); ApiError for another answer.</summary>
        object Api(string method, string path, Dictionary<string, List<string>> query);
    }

    /// <summary>An answer of the API other than 200 or 404: its HTTP status and what is said ({"error": message}).</summary>
    public sealed class ApiError : Exception
    {
        public int Status { get; }
        /// <summary>What is said in Chinese ({"errorZh": ...}); null: only the English.</summary>
        public string MessageZh { get; }
        public ApiError(int status, string message, string messageZh = null) : base(message) { Status = status; MessageZh = messageZh; }
    }
}
