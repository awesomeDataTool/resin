using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using log4net;
using Resin.IO;

namespace Resin
{
    /// <summary>
    /// A reader that provides thread-safe access to an index
    /// </summary>
    public class Searcher : IDisposable
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(Searcher));
        private readonly string _directory;
        private readonly QueryParser _parser;
        private readonly IScoringScheme _scorer;
        private readonly ConcurrentDictionary<string, LazyTrie> _trieFiles;
        private readonly IxInfo _ix;
        private readonly ConcurrentDictionary<string, PostingsContainer> _postingContainers;
        private readonly ConcurrentDictionary<string, DocContainer> _docContainers;

        public Searcher(string directory, QueryParser parser, IScoringScheme scorer)
        {
            _directory = directory;
            _parser = parser;
            _scorer = scorer;
            _trieFiles = new ConcurrentDictionary<string, LazyTrie>();
            _docContainers = new ConcurrentDictionary<string, DocContainer>();
            _postingContainers = new ConcurrentDictionary<string, PostingsContainer>();

            _ix = IxInfo.Load(Path.Combine(_directory, "0.ix"));
        }

        public Result Search(string query, int page = 0, int size = 10000, bool returnTrace = false)
        {
            var timer = new Stopwatch();
            var collector = new Collector(_directory, _ix, _trieFiles, _postingContainers);
            timer.Start();
            var q = _parser.Parse(query);
            if (q == null)
            {
                return new Result{Docs = Enumerable.Empty<IDictionary<string, string>>()};
            }
            Log.DebugFormat("parsed query {0} in {1}", q, timer.Elapsed);
            var scored = collector.Collect(q, page, size, _scorer).ToList();
            var skip = page*size;
            var paged = scored.Skip(skip).Take(size).ToDictionary(x => x.DocId, x => x);
            var docs = paged.Values.Select(s => GetDoc(s.DocId)); 
            return new Result { Docs = docs, Total = scored.Count};
        }

        private IDictionary<string, string> GetDoc(string docId)
        {
            var bucketId = docId.ToDocBucket();
            DocContainer container;
            if (!_docContainers.TryGetValue(bucketId, out container))
            {
                var fileName = Path.Combine(_directory, bucketId + ".dix");
                container = DocContainer.Load(fileName);
                _docContainers[bucketId] = container;
            }
            return container.Get(docId, _directory).Fields;
        }

        public void Dispose()
        {
            foreach (var dc in _docContainers.Values)
            {
                dc.Dispose();
            }

            foreach (var dc in _postingContainers.Values)
            {
                //dc.Dispose();
            }
        }
    }
}