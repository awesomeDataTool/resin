﻿using StreamIndex;
using System;
using System.IO;
using System.Linq;
using Resin.Documents;

namespace Resin
{
    public class FullTextReadSessionFactory : IReadSessionFactory, IDisposable
    {
        public string DirectoryName { get { return _directory; } }

        private readonly string _directory;
        private readonly FileStream _data;

        public FullTextReadSessionFactory(string directory, int bufferSize = 4096*12)
        {
            _directory = directory;

            var version = Directory.GetFiles(directory, "*.ix")
                .Select(f => long.Parse(Path.GetFileNameWithoutExtension(f)))
                .OrderBy(v => v).First();

            var _dataFn = Path.Combine(_directory, version + ".rdb");

            _data = new FileStream(
                _dataFn,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize,
                FileOptions.RandomAccess);
        }

        public IReadSession OpenReadSession(long version)
        {
            var ix = FullTextSegmentInfo.Load(Path.Combine(_directory, version + ".ix"));

            return OpenReadSession(ix);
        }

        public IReadSession OpenReadSession(SegmentInfo ix)
        {
            return new FullTextReadSession(
                ix,
                new DocHashReader(_data, ix.DocHashOffset),
                new BlockInfoReader(_data, ix.DocAddressesOffset),
                _data);
        }

        public void Dispose()
        {
            _data.Dispose();
        }
    }
}
