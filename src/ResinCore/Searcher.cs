using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using log4net;
using Resin.Analysis;
using Resin.Querying;
using Resin.Sys;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using StreamIndex;
using DocumentTable;

namespace Resin
{
    /// <summary>
    /// Query indices in a directory.
    /// </summary>
    public class Searcher : IDisposable
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(Searcher));
        private readonly string _directory;
        private readonly QueryParser _parser;
        private readonly IScoringSchemeFactory _scorerFactory;
        private readonly IList<BatchInfo> _ixs;
        private readonly int _blockSize;
        private readonly int _documentCount;
        private readonly IReadSessionFactory _sessionFactory;

        public Searcher(string directory)
            :this(directory, new QueryParser(new Analyzer()), new TfIdfFactory())
        {
        }

        public Searcher(string directory, QueryParser parser, IScoringSchemeFactory scorerFactory, IReadSessionFactory sessionFactory = null)
        {
            _directory = directory;
            _parser = parser;
            _scorerFactory = scorerFactory;

            _ixs = Util.GetIndexFileNamesInChronologicalOrder(directory).Select(BatchInfo.Load).ToList();

            _documentCount = Util.GetDocumentCount(_ixs);

            _blockSize = BlockSerializer.SizeOfBlock();

            _sessionFactory = sessionFactory ?? new ReadSessionFactory();
        }

        public Result Search(string query, int page = 0, int size = 10000)
        {
            var searchTime = new Stopwatch();
            searchTime.Start();

            var queryContext = _parser.Parse(query);

            if (queryContext == null)
            {
                return new Result { Docs = new List<ScoredDocument>() };
            }

            var skip = page * size;
            var scored = Collect(queryContext);
            var paged = scored.Skip(skip).Take(size).ToList();
            var docs = new ConcurrentBag<ScoredDocument>();
            var result = new Result { Total = scored.Count};
            var groupedByIx = paged.GroupBy(s => s.Ix);

            var docTime = new Stopwatch();
            docTime.Start();

            Parallel.ForEach(groupedByIx, group =>
            //foreach(var group in groupedByIx)
            {
                GetDocs(group.ToList(), group.Key, docs);
            });

            result.Docs = docs.OrderByDescending(d => d.Score).ToList();
            result.QueryTerms = queryContext.ToList()
                .Where(q => q.Terms != null)
                .SelectMany(q => q.Terms.Select(t => t.Word.Value))
                .Distinct()
                .ToArray();

            Log.DebugFormat("fetched {0} docs for query {1} in {2}", docs.Count, queryContext, docTime.Elapsed);
            Log.DebugFormat("searched {0} in {1}", queryContext, searchTime.Elapsed);

            return result;
        }

        private IList<DocumentScore> Collect(QueryContext query)
        {
            var results = new List<IList<DocumentScore>>(_ixs.Count);

            foreach (var ix in _ixs)
            {
                using (var collector = new Collector(_directory, ix, _scorerFactory, _documentCount))
                {
                    results.Add(collector.Collect(query));
                }
            }

            var timer = new Stopwatch();
            timer.Start();

            if (results.Count == 1)
            {
                Log.DebugFormat("reduced collection results for term query {0} in {1}", query, timer.Elapsed);

                return results[0];
            }
            
            var agg = results.CombineTakingLatestVersion().ToList();

            Log.DebugFormat("reduced collection results for phrase query {0} in {1}", query, timer.Elapsed);

            return agg;
        }

        private void GetDocs(IList<DocumentScore> scores, BatchInfo ix, ConcurrentBag<ScoredDocument> result)
        {
            var documentIds = scores.Select(s => s.DocumentId).ToList();

            var docFileName = Path.Combine(_directory, ix.VersionId + ".dtbl");

            using (var session = _sessionFactory.OpenReadSession(docFileName, ix))
            {
                var dic = scores.ToDictionary(x => x.DocumentId, y => y.Score);

                foreach (var doc in session.Read(documentIds, ix))
                {
                    var score = dic[doc.Id];

                    result.Add(new ScoredDocument(doc, score));
                }
            }
        }

        public void Dispose()
        {
        }
    }
}