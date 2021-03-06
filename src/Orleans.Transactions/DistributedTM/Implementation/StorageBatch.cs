﻿using Newtonsoft.Json;
using Orleans.Concurrency;
using Orleans.Serialization;
using Orleans.Transactions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Transactions.DistributedTM
{
    /// <summary>
    /// Events streamed to storage. 
    /// </summary>
    public interface ITransactionalStateStorageEvents<TState> where TState : class, new()
    {
        void Prepare(long sequenceNumber, Guid transactionId, DateTime timestamp, ITransactionParticipant transactionManager, TState state);

        void Read(DateTime timestamp);

        void Cancel(long sequenceNumber);

        void Confirm(long sequenceNumber);

        void Commit(Guid transactionId, DateTime timestamp, List<ITransactionParticipant> writeParticipants);

        void Collect(Guid transactionId);
    }

    /// <summary>
    /// Metadata is stored in storage, as a JSON object
    /// </summary>
    [Serializable]
    public class MetaData
    {
        public long StableSequenceNumber { get; set; }

        public DateTime TimeStamp { get; set; }

        public Dictionary<Guid, CommitRecord> CommitRecords { get; set; }

        public static JsonSerializerSettings SerializerSettings { get; set; }
    }

    [Serializable]
    [Immutable]
    public class CommitRecord
    {
        public DateTime Timestamp { get; set; }

        public List<ITransactionParticipant> WriteParticipants { get; set; }
    }

    /// <summary>
    /// Accumulates storage events, for submitting them to storage as a batch.
    /// </summary>
    /// <typeparam name="TState"></typeparam>
    internal class StorageBatch<TState> : ITransactionalStateStorageEvents<TState>
        where TState : class, new()
    {
        // watermarks for commit, prepare, abort
        private long confirmUpTo;
        private long cancelAbove;
        private long cancelAboveStart;

        // prepare records
        private SortedDictionary<long, PendingTransactionState<TState>> prepares;

        // follow-up actions, to be executed after storing this batch
        private List<Action> followUpActions;

        // counters for each type of event
        private int total = 0;
        private int prepare = 0;
        private int read = 0;
        private int commit = 0;
        private int confirm = 0;
        private int collect = 0;
        private int cancel = 0;

        public MetaData MetaData { get; private set; }

        public string ETag { get; set; }

        public int BatchSize => total;

        public override string ToString()
        {
            return $"batchsize={total} [{read}r {prepare}p {commit}c {confirm}cf {collect}cl {cancel}cc]";
        }

        public StorageBatch(TransactionalStorageLoadResponse<TState> loadresponse)
        {
            MetaData = ReadMetaData(loadresponse);
            ETag = loadresponse.ETag;
            confirmUpTo = MetaData.StableSequenceNumber;
            cancelAbove = loadresponse.PendingStates?.LastOrDefault()?.SequenceId ?? MetaData.StableSequenceNumber;
            cancelAboveStart = cancelAbove;
        }

        public StorageBatch(StorageBatch<TState> previous)
        {
            MetaData = previous.MetaData;
            confirmUpTo = previous.confirmUpTo;
            cancelAbove = previous.cancelAbove;
            cancelAboveStart = cancelAbove;
        }

        private static MetaData ReadMetaData(TransactionalStorageLoadResponse<TState> loadresponse)
        {
            if (string.IsNullOrEmpty(loadresponse.Metadata))
            {
                // this thing is fresh... did not exist in storage yet
                return new MetaData()
                {
                    StableSequenceNumber = 0,
                    TimeStamp = default(DateTime),
                    CommitRecords = new Dictionary<Guid, CommitRecord>(),
                };
            }
            else
            {
                return JsonConvert.DeserializeObject<MetaData>(loadresponse.Metadata, MetaData.SerializerSettings);
            }
        }

        public Task<string> Store(ITransactionalStateStorage<TState> storage, string stateName)
        {
            var jsonMetaData = JsonConvert.SerializeObject(MetaData, MetaData.SerializerSettings);

            var list = new List<PendingTransactionState<TState>>();

            if (prepares != null)
            {
                foreach (var kvp in prepares)
                {
                    list.Add(kvp.Value);
                }
            }

            return storage.Store(stateName, ETag, jsonMetaData, list,
                (confirm > 0) ? confirmUpTo : (long?)null,
                (cancelAbove < cancelAboveStart) ? cancelAbove : (long?)null);
        }

        public void RunFollowUpActions()
        {
            if (followUpActions != null)
            {
                foreach (var action in followUpActions)
                {
                    action();
                }
            }
        }

        #region storage events

        public void Read(DateTime timestamp)
        {
            read++;
            total++;

            if (MetaData.TimeStamp < timestamp)
            {
                MetaData.TimeStamp = timestamp;
            }
        }

        public void Prepare(long sequenceNumber, Guid transactionId, DateTime timestamp,
          ITransactionParticipant transactionManager, TState state)
        {
            prepare++;
            total++;

            if (MetaData.TimeStamp < timestamp)
                MetaData.TimeStamp = timestamp;

            if (prepares == null)
                prepares = new SortedDictionary<long, PendingTransactionState<TState>>();

            var tmstring = (transactionManager == null) ? null :
                JsonConvert.SerializeObject(transactionManager, MetaData.SerializerSettings);

            prepares[sequenceNumber] = new PendingTransactionState<TState>()
            {
                SequenceId = sequenceNumber,
                State = state,
                TimeStamp = timestamp,
                TransactionId = transactionId.ToString(),
                TransactionManager = tmstring
            };

            if (cancelAbove < sequenceNumber)
            {
                cancelAbove = sequenceNumber;
            }
        }

        public void Cancel(long sequenceNumber)
        {
            cancel++;
            total++;

            if (prepares != null)
            {
                prepares.Remove(sequenceNumber);
            }

            if (cancelAbove > sequenceNumber - 1)
            {
                cancelAbove = sequenceNumber - 1;
            }
        }

        public void Confirm(long sequenceNumber)
        {
            confirm++;
            total++;

            confirmUpTo = sequenceNumber;

            MetaData.StableSequenceNumber = sequenceNumber;

            // remove all redundant prepare records that are superseded by a later confirmed state
            while (true)
            {
                long? first = prepares?.FirstOrDefault().Value?.SequenceId;

                if (first.HasValue && first < confirmUpTo)
                {
                    prepares.Remove(first.Value);
                }
                else
                {
                    break;
                }
            }
        }

        public void Commit(Guid transactionId, DateTime timestamp, List<ITransactionParticipant> writeParticipants)
        {
            commit++;
            total++;

            MetaData.CommitRecords.Add(transactionId, new CommitRecord()
            {
                Timestamp = timestamp,
                WriteParticipants = writeParticipants
            });
        }

        public void Collect(Guid transactionId)
        {
            collect++;
            total++;

            MetaData.CommitRecords.Remove(transactionId);
        }

        #endregion

        public void FollowUpAction(Action action)
        {
            if (followUpActions == null)
            {
                followUpActions = new List<Action>();
            }
            followUpActions.Add(action);
        }
    }
}
