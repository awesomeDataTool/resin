﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using log4net;
using Resin.IO;

namespace Resin
{
    public class Collector : IDisposable
    {
        private readonly string _directory;
        private static readonly ILog Log = LogManager.GetLogger(typeof(Collector));
        private readonly IndexInfo _ix;
        private readonly ConcurrentDictionary<ulong, TermDocumentMatrix> _termDocMatrises;

        public Collector(string directory, IndexInfo ix)
        {
            _directory = directory;
            _ix = ix;
            _termDocMatrises = new ConcurrentDictionary<ulong, TermDocumentMatrix>();
        }

        public IEnumerable<DocumentScore> Collect(QueryContext queryContext, int page, int size, IScoringScheme scorer)
        {
            Expand(queryContext);
            Scan(queryContext, scorer);
            var scored = queryContext.Resolve().Values.OrderByDescending(s => s.Score).ToList();
            return scored;
        }

        private TermDocumentMatrix GetMatrix(Term term)
        {
            var key = term.Token.Substring(0, 1).ToHash();
            TermDocumentMatrix matrix;
            if (!_termDocMatrises.TryGetValue(key, out matrix))
            {
                string fileId;
                if (!_ix.TermFileIds.TryGetValue(term.Token.Substring(0, 1).ToHash(), out fileId))
                {
                    return null;
                }
                var fileName = Path.Combine(_directory, fileId + ".tdm");
                matrix = TermDocumentMatrix.Load(fileName);
                _termDocMatrises.AddOrUpdate(key, matrix, (term1, documentMatrix) => documentMatrix);
            }
            return matrix;
        }

        private TrieScanner GetTrie(string field)
        {
            var timer = new Stopwatch();
            timer.Start();
            var fileName = Path.Combine(_directory, field.ToTrieContainerId() + ".tc");
            var fs = File.Open(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            var reader = new TrieStreamReader(fs);
            Log.DebugFormat("opened {0} in {1}", fileName, timer.Elapsed);
            return reader.Reset();
        }

        private void Scan(QueryContext queryContext, IScoringScheme scorer)
        {
            queryContext.Result = GetScoredResult(queryContext, scorer).ToDictionary(x => x.DocId, y => y);
            foreach (var child in queryContext.Children)
            {
                Scan(child, scorer);
            }
        }

        private IEnumerable<DocumentScore> GetScoredResult(QueryTerm queryTerm, IScoringScheme scoringScheme)
        {
            using (var trie = GetTrie(queryTerm.Field))
            {
                if (trie == null) yield break;

                var totalNumOfDocs = _ix.DocumentCount.DocCount[queryTerm.Field];
                if (trie.HasWord(queryTerm.Value))
                {
                    var matrix = GetMatrix(new Term(queryTerm.Field, queryTerm.Value));
                    if (matrix == null) yield break;

                    var weights = matrix.Weights[new Term(queryTerm.Field, queryTerm.Value)];
                    var scorer = scoringScheme.CreateScorer(totalNumOfDocs, weights.Count);
                    foreach (var weight in weights)
                    {
                        var hit = new DocumentScore(weight.DocumentId, weight.Weight, totalNumOfDocs);
                        scorer.Score(hit);
                        yield return hit;
                    }
                }
            }
        }

        private void Expand(QueryContext queryContext)
        {
            if (queryContext == null) throw new ArgumentNullException("queryContext");
            if (queryContext.Fuzzy || queryContext.Prefix)
            {
                var timer = new Stopwatch();
                timer.Start();

                using (var trie = GetTrie(queryContext.Field))
                {
                    IList<QueryContext> expanded = null;

                    if (queryContext.Fuzzy)
                    {
                        expanded = trie.Similar(queryContext.Value, queryContext.Edits).Select(token => new QueryContext(queryContext.Field, token)).ToList();
                    }
                    else if (queryContext.Prefix)
                    {
                        expanded = trie.Prefixed(queryContext.Value).Select(token => new QueryContext(queryContext.Field, token)).ToList();
                    }

                    if (expanded != null)
                    {
                        foreach (var t in expanded.Where(e => e.Value != queryContext.Value))
                        {
                            queryContext.Children.Add(t);
                        }
                    }

                    queryContext.Prefix = false;
                    queryContext.Fuzzy = false;

                    Log.DebugFormat("expanded {0} in {1}", queryContext, timer.Elapsed);  
                }
            }
            foreach (var child in queryContext.Children)
            {
                Expand(child);
            }
        }

        public void Dispose()
        {
        }
    }
}