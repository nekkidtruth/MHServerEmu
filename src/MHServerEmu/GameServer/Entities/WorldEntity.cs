﻿using System.Text;
using Google.ProtocolBuffers;
using MHServerEmu.Common.Extensions;
using MHServerEmu.GameServer.Common;
using MHServerEmu.GameServer.Powers;
using MHServerEmu.GameServer.Properties;

namespace MHServerEmu.GameServer.Entities
{
    public class WorldEntity : Entity
    {
        public PrototypeCollectionEntry[] UnknownPrototypes { get; set; }
        public Condition[] Conditions { get; set; }
        public int UnknownPowerVar { get; set; }

        public WorldEntity(byte[] archiveData)
        {
            CodedInputStream stream = CodedInputStream.CreateInstance(archiveData);

            ReadEntityFields(stream);
            ReadWorldEntityFields(stream);
            ReadUnknownFields(stream);
        }

        public WorldEntity() { }

        public WorldEntity(ulong replicationId, Vector3 mapPosition, int health, int mapAreaId,
            int healthMaxOther, ulong mapRegionId, int mapCellId, ulong contextAreaRef)
        {
            ReplicationPolicy = 0x20;
            ReplicationId = replicationId;

            Properties = new Property[]
            {
                new(0x204D),    // MapPosition
                new(0x203B),    // Health
                new(0xA04C),    // MapAreaId
                new(0x203C),    // HealthMaxOther
                new(0x404D),    // MapRegionId
                new(0xC04C),    // MapCellId
                new(0x6019)     // ContextAreaRef
            };

            Properties[0].Value.Set(mapPosition);
            Properties[1].Value.Set(health);
            Properties[2].Value.Set(mapAreaId);
            Properties[3].Value.Set(healthMaxOther);
            Properties[4].Value.Set(mapRegionId);
            Properties[5].Value.Set(mapCellId);
            Properties[6].Value.Set(contextAreaRef);

            UnknownPrototypes = Array.Empty<PrototypeCollectionEntry>();
            Conditions = Array.Empty<Condition>();
            UnknownPowerVar = 0;
        }

        public override byte[] Encode()
        {
            using (MemoryStream memoryStream = new())
            {
                CodedOutputStream stream = CodedOutputStream.CreateInstance(memoryStream);

                WriteEntityFields(stream);
                WriteWorldEntityFields(stream);
                WriteUnknownFields(stream);

                stream.Flush();
                return memoryStream.ToArray();
            }
        }

        public override string ToString()
        {
            using (MemoryStream stream = new())
            using (StreamWriter writer = new(stream))
            {
                WriteEntityString(writer);
                WriteWorldEntityString(writer);
                WriteUnknownFieldString(writer);

                writer.Flush();
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        protected void ReadWorldEntityFields(CodedInputStream stream)
        {
            UnknownPrototypes = new PrototypeCollectionEntry[stream.ReadRawVarint64()];
            for (int i = 0; i < UnknownPrototypes.Length; i++)
                UnknownPrototypes[i] = new(stream);

            Conditions = new Condition[stream.ReadRawVarint64()];
            for (int i = 0; i < Conditions.Length; i++)
                Conditions[i] = new(stream);

            // Gazillion::PowerCollection::SerializeRecordCount
            UnknownPowerVar = stream.ReadRawInt32();
        }

        protected void WriteWorldEntityFields(CodedOutputStream stream)
        {
            stream.WriteRawVarint64((ulong)UnknownPrototypes.Length);
            foreach (PrototypeCollectionEntry entry in UnknownPrototypes)
                stream.WriteRawBytes(entry.Encode());

            stream.WriteRawVarint64((ulong)Conditions.Length);
            foreach (Condition condition in Conditions)
                stream.WriteRawBytes(condition.Encode());

            stream.WriteRawInt32(UnknownPowerVar);
        }

        protected void WriteWorldEntityString(StreamWriter writer)
        {
            for (int i = 0; i < UnknownPrototypes.Length; i++)
                writer.WriteLine($"UnknownPrototype{i}: {UnknownPrototypes[i]}");

            for (int i = 0; i < Conditions.Length; i++)
                writer.WriteLine($"Condition{i}: {Conditions[i]}");

            writer.WriteLine($"UnknownPowerVar: 0x{UnknownPowerVar.ToString("X")}");
        }
    }
}