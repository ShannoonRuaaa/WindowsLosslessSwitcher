using System.Buffers.Binary;
using System.IO;

namespace WindowsLosslessSwitcher.Services;

public sealed class BinaryPlistReader
{
    public Dictionary<string, object?> ReadRootDictionary(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 16 * 1024 * 1024) throw new InvalidDataException("Plist is too large.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return ReadRootDictionary(bytes);
    }

    public Dictionary<string, object?> ReadRootDictionary(byte[] bytes)
    {
        try
        {
            var parser = new Parser(bytes);
            return parser.ReadRootDictionary();
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            throw new InvalidDataException("Malformed binary plist.", ex);
        }
    }

    private sealed class Parser
    {
        private readonly byte[] _bytes;
        private readonly HashSet<int> _active = [];
        private readonly Dictionary<int, object?> _objects = [];
        private int _referencesRead;
        private readonly int _offsetSize;
        private readonly int _objectRefSize;
        private readonly long _numObjects;
        private readonly long _topObject;
        private readonly long _offsetTableOffset;

        public Parser(byte[] bytes)
        {
            _bytes = bytes;
            if (_bytes.Length > 16 * 1024 * 1024 || _bytes.Length < 40 || !_bytes.AsSpan(0, 8).SequenceEqual("bplist00"u8))
            {
                throw new InvalidDataException("Unsupported plist format.");
            }

            var trailer = _bytes.AsSpan(_bytes.Length - 32, 32);
            _offsetSize = trailer[6];
            _objectRefSize = trailer[7];
            _numObjects = ReadSizedUnsignedInteger(trailer[8..16]);
            _topObject = ReadSizedUnsignedInteger(trailer[16..24]);
            _offsetTableOffset = ReadSizedUnsignedInteger(trailer[24..32]);
            if (_offsetSize is not (1 or 2 or 4 or 8) || _objectRefSize is not (1 or 2 or 4 or 8) ||
                _numObjects <= 0 || _numObjects > 100000 || _topObject >= _numObjects ||
                _offsetTableOffset < 8 || _offsetTableOffset > _bytes.Length - 32 ||
                _numObjects > (_bytes.Length - 32 - _offsetTableOffset) / _offsetSize)
                throw new InvalidDataException("Invalid binary plist trailer.");
        }

        public Dictionary<string, object?> ReadRootDictionary()
        {
            var root = ReadObject((int)_topObject);
            return root as Dictionary<string, object?> ?? throw new InvalidDataException("Root object is not a dictionary.");
        }

        private object? ReadObject(int objectIndex)
        {
            if (++_referencesRead > 1000000 || _active.Count >= 128 || !_active.Add(objectIndex))
                throw new InvalidDataException("Cyclic or excessively complex plist.");
            try
            {
                if (_objects.TryGetValue(objectIndex, out var cached)) return cached;
                var result = ReadObjectCore(objectIndex);
                _objects[objectIndex] = result;
                return result;
            }
            finally { _active.Remove(objectIndex); }
        }
        private object? ReadObjectCore(int objectIndex)
        {
            var offset = checked((int)ReadOffset(objectIndex));
            ValidateRange(offset, 1);
            var marker = _bytes[offset];
            var objectType = marker >> 4;
            var objectInfo = marker & 0x0F;

            return objectType switch
            {
                0x0 => objectInfo switch
                {
                    0x0 => null,
                    0x8 => false,
                    0x9 => true,
                    _ => null,
                },
                0x1 => ReadInteger(offset, objectInfo),
                0x2 => ReadReal(offset, objectInfo),
                0x4 => ReadData(offset, objectInfo),
                0x5 => ReadAsciiString(offset, objectInfo),
                0x6 => ReadUtf16String(offset, objectInfo),
                0xA => ReadArray(offset, objectInfo),
                0xD => ReadDictionary(offset, objectInfo),
                _ => null,
            };
        }

        private long ReadInteger(int offset, int objectInfo)
        {
            var byteCount = 1 << objectInfo;
            var data = ObjectBytes(offset + 1, byteCount);
            return byteCount == 8 ? BinaryPrimitives.ReadInt64BigEndian(data) : ReadSizedUnsignedInteger(data);
        }

        private double ReadReal(int offset, int objectInfo)
        {
            var byteCount = 1 << objectInfo;
            var data = ObjectBytes(offset + 1, byteCount);
            return byteCount switch
            {
                4 => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(data)),
                8 => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(data)),
                _ => 0,
            };
        }

        private byte[] ReadData(int offset, int objectInfo)
        {
            var (count, headerLength) = ReadCount(offset, objectInfo);
            var start = offset + headerLength;
            return ObjectBytes(start, count).ToArray();
        }

        private string ReadAsciiString(int offset, int objectInfo)
        {
            var (count, headerLength) = ReadCount(offset, objectInfo);
            return System.Text.Encoding.ASCII.GetString(ObjectBytes(offset + headerLength, count));
        }

        private string ReadUtf16String(int offset, int objectInfo)
        {
            var (count, headerLength) = ReadCount(offset, objectInfo);
            return System.Text.Encoding.BigEndianUnicode.GetString(ObjectBytes(offset + headerLength, checked(count * 2)));
        }

        private List<object?> ReadArray(int offset, int objectInfo)
        {
            var (count, headerLength) = ReadCount(offset, objectInfo);
            ValidateRange(offset + headerLength, checked(count * _objectRefSize));
            var list = new List<object?>(count);
            var refsOffset = offset + headerLength;
            for (var i = 0; i < count; i++)
            {
                var objectRef = ReadObjectReference(refsOffset + (i * _objectRefSize));
                list.Add(ReadObject(objectRef));
            }

            return list;
        }

        private Dictionary<string, object?> ReadDictionary(int offset, int objectInfo)
        {
            var (count, headerLength) = ReadCount(offset, objectInfo);
            ValidateRange(offset + headerLength, checked(count * _objectRefSize * 2));
            var dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
            var keyRefsOffset = offset + headerLength;
            var valueRefsOffset = keyRefsOffset + (count * _objectRefSize);

            for (var i = 0; i < count; i++)
            {
                var keyRef = ReadObjectReference(keyRefsOffset + (i * _objectRefSize));
                var valueRef = ReadObjectReference(valueRefsOffset + (i * _objectRefSize));
                var key = ReadObject(keyRef) as string;
                if (!string.IsNullOrEmpty(key))
                {
                    dictionary[key] = ReadObject(valueRef);
                }
            }

            return dictionary;
        }

        private (int Count, int HeaderLength) ReadCount(int offset, int objectInfo)
        {
            if (objectInfo < 0x0F)
            {
                return (objectInfo, 1);
            }

            var lengthMarkerOffset = offset + 1;
            var marker = ObjectBytes(lengthMarkerOffset, 1)[0];
            var markerType = marker >> 4;
            var markerInfo = marker & 0x0F;
            if (markerType != 0x1)
            {
                throw new InvalidDataException("Unexpected length marker in binary plist.");
            }

            var byteCount = 1 << markerInfo;
            var count = checked((int)ReadSizedUnsignedInteger(ObjectBytes(lengthMarkerOffset + 1, byteCount)));
            if (count > 100000) throw new InvalidDataException("Plist collection is too large.");
            return (count, 2 + byteCount);
        }

        private int ReadObjectReference(int offset) =>
            checked((int)ReadSizedUnsignedInteger(ObjectBytes(offset, _objectRefSize)));

        private long ReadOffset(int objectIndex)
        {
            if (objectIndex < 0 || objectIndex >= _numObjects) throw new InvalidDataException("Invalid object reference.");
            var offsetPosition = checked((int)_offsetTableOffset + objectIndex * _offsetSize);
            return ReadSizedUnsignedInteger(_bytes.AsSpan(offsetPosition, _offsetSize));
        }

        private void ValidateRange(int start, int count)
        {
            if (start < 8 || count < 0 || start > _offsetTableOffset || count > _offsetTableOffset - start)
                throw new InvalidDataException("Object exceeds plist data bounds.");
        }
        private ReadOnlySpan<byte> ObjectBytes(int start, int count)
        {
            ValidateRange(start, count);
            return _bytes.AsSpan(start, count);
        }
        private static long ReadSizedUnsignedInteger(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length is not (1 or 2 or 4 or 8) || (bytes.Length == 8 && bytes[0] > 127))
                throw new InvalidDataException("Unsupported integer size or value.");
            long result = 0;
            foreach (var value in bytes)
            {
                result = (result << 8) | value;
            }

            return result;
        }
    }
}
