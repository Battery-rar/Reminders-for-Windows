using System.Text;

namespace Reminders.Core;

internal static partial class AppleDocument
{
    // Account list ordering uses a CRArray. Its sequence stores UUID attachments,
    // while a dictionary maps each attachment identity to a List identifier.
    // Preserve existing entries and causal history when adding to that sequence.
    internal static string AppendList(string archive, string listId, out bool alreadyPresent)
    {
        alreadyPresent = false;
        try
        {
            var document = WireMessage.Read(Expand(archive));
            if (document.Has(2)) throw new InvalidDataException(); // A delta cannot replace an account snapshot.
            var version = document.Message(1);
            var timestamp = document.Message(7);
            var objects = document.Messages(3);
            var root = objects[0];
            var array = root.Message(15);
            var stringArray = array.Message(1);
            var sequence = stringArray.Message(1);
            var dictionary = array.Message(2);
            var documentUuids = document.Blobs(6).ToArray();
            var types = document.Blobs(5).Select(Encoding.UTF8.GetString).ToList();
            var keys = document.Blobs(4).Select(Encoding.UTF8.GetString).ToList();
            var dictionaryEntries = dictionary.Messages(1);
            const string stringClass = "com.apple.CRDT.NSString", uuidClass = "com.apple.CRDT.NSUUID";
            var stringType = types.IndexOf(stringClass);
            var uuidType = types.IndexOf(uuidClass);
            if (documentUuids.Any(uuid => uuid.Length != 16)) throw new InvalidDataException();

            var attachments = stringArray.Messages(2);
            if (stringType < 0 || uuidType < 0)
            {
                if (objects.Count != 1 || dictionaryEntries.Count != 0 || attachments.Count != 0) throw new InvalidDataException();
                stringType = OrderingTableIndex(document, 5, types, stringClass);
                uuidType = OrderingTableIndex(document, 5, types, uuidClass);
            }
            var attachmentIds = attachments.Select(attachment => Convert.ToHexString(attachment.Blob(2))).ToHashSet(StringComparer.Ordinal);
            if (attachmentIds.Count != attachments.Count) throw new InvalidDataException();
            var matchedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in dictionaryEntries)
            {
                if (!entry.Has(2)) continue; // Tombstones have no value.
                var key = objects[checked((int)entry.Message(1).Integer(6))].Message(13);
                var value = objects[checked((int)entry.Message(2).Integer(6))].Message(13);
                if (key.Integer(1) != (ulong)uuidType || value.Integer(1) != (ulong)stringType) throw new InvalidDataException();
                var keyMapping = key.Messages(3).Single();
                var valueMapping = value.Messages(3).Single();
                if (keys[checked((int)keyMapping.Integer(1))] != "UUIDIndex" || keys[checked((int)valueMapping.Integer(1))] != "self") throw new InvalidDataException();
                var attachmentId = Convert.ToHexString(documentUuids[checked((int)keyMapping.Message(2).Integer(2))]);
                if (!attachmentIds.Contains(attachmentId)) continue;
                if (!matchedIds.Add(attachmentId)) throw new InvalidDataException();
                var existingId = Encoding.UTF8.GetString(valueMapping.Message(2).Blob(4));
                alreadyPresent |= string.Equals(existingId, listId, StringComparison.OrdinalIgnoreCase);
            }
            if (matchedIds.Count != attachments.Count) throw new InvalidDataException();
            if (alreadyPresent) return archive;

            var nodes = sequence.Messages(3);
            var runs = sequence.Messages(5);
            var text = sequence.Has(2) ? Encoding.UTF8.GetString(sequence.Blob(2)) : "";
            if (text.Length != attachments.Count || text.Any(character => character != '\uFFFC') ||
                runs.Sum(run => checked((int)run.Integer(1))) != text.Length ||
                !attachments.Select((attachment, index) => attachment.Integer(1) == (ulong)index && attachment.Blob(2).Length == 16).All(valid => valid) ||
                sequence.Has(4) || nodes.Count < 2) throw new InvalidDataException();

            var clocks = timestamp.Messages(1);
            var clockIds = clocks.Select(clock => Convert.ToHexString(clock.Blob(1))).ToList();
            if (clocks.Any(clock => clock.Blob(1).Length != 16) ||
                !clockIds.SequenceEqual(clockIds.Distinct().Order(StringComparer.Ordinal))) throw new InvalidDataException();
            var start = nodes[0].Message(1);
            var end = nodes[^1].Message(1);
            if (start.Integer(1) != 0 || start.Integer(2) != 0 || nodes[0].Integer(2) != 0 ||
                end.Integer(1) != 0 || end.Integer(2) != uint.MaxValue || nodes[^1].Integer(2) != 0 || nodes[^1].Has(5))
                throw new InvalidDataException();

            var parents = new int[nodes.Count];
            ulong nextTextClock = 0;
            ulong liveLength = 0;
            for (var index = 0; index < nodes.Count; index++)
            {
                var node = nodes[index];
                var length = node.Integer(2);
                var identity = node.Message(1);
                var style = node.Message(3);
                if (identity.Integer(1) > (ulong)clocks.Count || style.Integer(1) > (ulong)clocks.Count) throw new InvalidDataException();
                foreach (var child in node.Integers(5))
                {
                    if (child <= (ulong)index || child >= (ulong)nodes.Count || ++parents[(int)child] != 1) throw new InvalidDataException();
                }
                if (length == 0) continue;
                if (identity.Integer(1) == 0 || node.Integer(4) > 1) throw new InvalidDataException();
                if (node.Integer(4) == 0) liveLength = checked(liveLength + length);
                nextTextClock = Math.Max(nextTextClock, checked(identity.Integer(2) + length));
            }
            if (parents.Skip(1).Any(parentCount => parentCount != 1) || liveLength != (ulong)text.Length || nextTextClock >= uint.MaxValue - 1)
                throw new InvalidDataException();

            var replicaBytes = UuidBytes();
            var replicaHex = Convert.ToHexString(replicaBytes);
            var insertion = clockIds.FindIndex(uuid => string.CompareOrdinal(uuid, replicaHex) > 0);
            if (insertion < 0) insertion = clocks.Count;
            var replica = (ulong)insertion + 1;
            var newSequenceClock = new WireMessage();
            newSequenceClock.SetBlob(1, replicaBytes);
            newSequenceClock.SetMessages(2, [NumberMessage(nextTextClock + 1), NumberMessage(1)]);
            clocks.Insert(insertion, newSequenceClock);
            timestamp.SetMessages(1, clocks);

            // Insert before the end sentinel. Existing character and style IDs
            // keep their UUIDs when the sorted vector gains a replica.
            var endIndex = (ulong)nodes.Count - 1;
            foreach (var node in nodes)
            {
                foreach (ulong field in new ulong[] { 1, 3 })
                {
                    var identity = node.Message(field);
                    if (identity.Integer(1) < replica) continue;
                    identity.SetInteger(1, identity.Integer(1) + 1);
                    node.SetMessage(field, identity);
                }
            }
            var newNode = new WireMessage();
            newNode.SetMessage(1, Identity(replica, nextTextClock));
            newNode.SetInteger(2, 1);
            newNode.SetMessage(3, Identity(replica, 0));
            newNode.SetInteger(5, endIndex + 1);
            nodes.Insert((int)endIndex, newNode);
            sequence.SetBlob(2, Encoding.UTF8.GetBytes(text + '\uFFFC'));
            sequence.SetMessages(3, nodes);
            sequence.SetMessages(5, runs.Append(NumberMessage(1)));

            var newAttachmentUuid = UuidBytes();
            var newAttachment = NumberMessage((ulong)text.Length);
            newAttachment.SetBlob(2, newAttachmentUuid);
            stringArray.SetMessage(1, sequence);
            stringArray.AddBlob(2, newAttachment.Write());

            var newVersionEntry = NumberMessage((ulong)documentUuids.Length);
            newVersionEntry.SetInteger(2, 1);
            newVersionEntry.SetInteger(3, 0);
            version.AddBlob(1, newVersionEntry.Write());
            var newEntryClock = new WireMessage();
            newEntryClock.SetMessage(1, newVersionEntry);
            var newEntry = new WireMessage();
            newEntry.SetMessage(1, NumberMessage((ulong)objects.Count + 1, 6));
            newEntry.SetMessage(2, NumberMessage((ulong)objects.Count, 6));
            newEntry.SetMessage(3, newEntryClock);
            dictionary.AddBlob(1, newEntry.Write());

            var stringId = new WireMessage();
            stringId.SetBlob(4, Encoding.UTF8.GetBytes(listId));
            objects.Add(OrderingObject(stringType, OrderingTableIndex(document, 4, keys, "self"), stringId));
            objects.Add(OrderingObject(uuidType, OrderingTableIndex(document, 4, keys, "UUIDIndex"), NumberMessage((ulong)documentUuids.Length + 1, 2)));
            array.SetMessage(1, stringArray);
            array.SetMessage(2, dictionary);
            root.SetMessage(15, array);
            document.SetMessage(1, version);
            document.SetMessages(3, objects);
            document.AddBlob(6, replicaBytes);
            document.AddBlob(6, newAttachmentUuid);
            document.SetMessage(7, timestamp);
            return Convert.ToBase64String(document.Write());
        }
        catch (Exception error) when (error is FormatException or InvalidDataException or IOException or OverflowException or InvalidOperationException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            throw ListCreation.OrderingUnavailable();
        }
    }

    private static byte[] UuidBytes() => Convert.FromHexString(Guid.NewGuid().ToString("N"));

    private static WireMessage NumberMessage(ulong value, ulong field = 1)
    {
        var message = new WireMessage();
        message.SetInteger(field, value);
        return message;
    }

    private static int OrderingTableIndex(WireMessage document, ulong field, List<string> items, string name)
    {
        var index = items.IndexOf(name);
        if (index >= 0) return index;
        document.AddBlob(field, Encoding.UTF8.GetBytes(name));
        items.Add(name);
        return items.Count - 1;
    }

    private static WireMessage OrderingObject(int type, int key, WireMessage value)
    {
        var mapping = NumberMessage((ulong)key);
        mapping.SetMessage(2, value);
        var item = NumberMessage((ulong)type);
        item.SetMessage(3, mapping);
        var custom = new WireMessage();
        custom.SetMessage(13, item);
        return custom;
    }
}
