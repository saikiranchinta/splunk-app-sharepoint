﻿using System;
using System.IO;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Text;

using Splunk.ModularInputs;

namespace Splunk.SharePoint2010.Audit
{
    class AuditDatabase
    {
        /// <summary>
        /// Ensure all mandatory fields are available and fill in defaults for the others.
        /// </summary>
        /// <param name="id">The SharePoint guid for this database object</param>
        /// <param name="dcs">The database connection string</param>
        /// <param name="dir">The checkpoint directory</param>
        public AuditDatabase(Guid farmId, Guid id, string dcs, string dir)
        {
            FarmId = farmId;
            Id = id;
            DatabaseConnectionString = dcs;
            CheckpointDirectory = dir;
            LastQuery = DateTime.MinValue;
            CheckSums = new List<int>();
        }

        /// <summary>
        /// The SharePoint Guid for this content database
        /// </summary>
        public Guid Id { get; set; }

        /// <summary>
        /// The SharePoint Farm Guid for this content database
        /// </summary>
        public Guid FarmId { get; set; }

        /// <summary>
        /// The database connection string for this content database
        /// </summary>
        public string DatabaseConnectionString { get; set; }

        /// <summary>
        /// The checkpoint directory for saving state
        /// </summary>
        public string CheckpointDirectory { get; set; }

        /// <summary>
        /// Returns the date/time stamp of the last entry received.
        /// </summary>
        public DateTime LastQuery { get; set; }

        /// <summary>
        /// Returns the list of checksum items that occurred in the last run.
        /// </summary>
        public List<int> CheckSums { get; set; }

        /// <summary>
        /// Load state for this database from the checkpoint directory.
        /// </summary>
        public void Load()
        {
            SystemLogger.Write(LogLevel.Debug, string.Format("Loading database {0}", Id));

            string sFileName = CheckpointFile();
            if (!File.Exists(sFileName))
            {
                SystemLogger.Write(LogLevel.Info, string.Format("File {0} does not exist - first run?  Skipping", sFileName));
                return;
            }
            SystemLogger.Write(LogLevel.Debug, string.Format("Loading checkpoint file {0}", sFileName));

            CheckSums.Clear();
            LastQuery = DateTime.MinValue;
            using (StreamReader reader = new StreamReader(sFileName))
            {
                string l;

                while ((l = reader.ReadLine()) != null)
                {
                    if (l.StartsWith("D:"))
                    {
                        LastQuery = new DateTime(long.Parse(l.Substring(2)));
                    }
                    else if (l.StartsWith("H:"))
                    {
                        int hash = int.Parse(l.Substring(2));
                        CheckSums.Add(hash);
                    }
                    else if (!(l.StartsWith("EOF") || l.StartsWith("V:")))
                    {
                        SystemLogger.Write(LogLevel.Warn, string.Format("Invalid Line {0} in checkpoint file - skipping", l));
                    }
                }
            }
            SystemLogger.Write(LogLevel.Debug, string.Format("Finished Loading checkpoint file {0}", sFileName));
        }

        /// <summary>
        /// Save state for this database for the checkpoint directory.
        /// </summary>
        public void Save()
        {
            SystemLogger.Write(LogLevel.Debug, string.Format("Saving database {0}", Id));

            string sFileName = CheckpointFile();
            using (StreamWriter writer = new StreamWriter(sFileName))
            {
                writer.WriteLine(string.Format("D:{0}", LastQuery.Ticks));
                foreach (int i in CheckSums)
                {
                    writer.WriteLine(string.Format("H:{0}", i));
                }
                writer.WriteLine(string.Format("V:1.0"));
                writer.WriteLine(string.Format("EOF"));
                writer.Flush();
            }

            SystemLogger.Write(LogLevel.Debug, string.Format("Finished Saving checkpoint file {0}", sFileName));
        }

        public List<AuditRecord> GetLatestEntries()
        {
            SystemLogger.Write(LogLevel.Debug, string.Format("Entering GetLatestEntries for database {0}", Id));
            SqlConnection oSqlConnection = GetConnection();
            List<AuditRecord> oAuditRecords = new List<AuditRecord>();

            string query = GetQueryString();
            SystemLogger.Write(LogLevel.Debug, string.Format("Query String is {0}", query));
            SqlCommand oQuery = new SqlCommand(query, oSqlConnection);
            SystemLogger.Write(LogLevel.Debug, "Executing Query");
            SqlDataReader row = oQuery.ExecuteReader();
            SystemLogger.Write(LogLevel.Debug, "Processing Results");
            while (row.Read())
            {
                // Create the new Audit Record object
                AuditRecord oAuditRecord = new AuditRecord
                {
                    CheckSum        = row.GetInt32(row.GetOrdinal("CheckSum")),
                    FarmId          = this.FarmId,
                    SiteId          = row.GetGuid(row.GetOrdinal("SiteId")),
                    ItemId          = row.GetGuid(row.GetOrdinal("ItemId")),
                    ItemType        = row.GetInt16(row.GetOrdinal("ItemType")),
                    UserId          = (row.IsDBNull(row.GetOrdinal("UserId")) ? -1 : row.GetInt32(row.GetOrdinal("UserId"))),
                    DocLocation     = (row.IsDBNull(row.GetOrdinal("DocLocation")) ? "" : row.GetString(row.GetOrdinal("DocLocation"))),
                    LocationType    = (row.IsDBNull(row.GetOrdinal("LocationType")) ? (byte)0xFF : row.GetByte(row.GetOrdinal("Locationtype"))),
                    Occurred        = row.GetDateTime(row.GetOrdinal("Occurred")),
                    Event           = row.GetInt32(row.GetOrdinal("Event")),
                    EventName       = (row.IsDBNull(row.GetOrdinal("EventName")) ? "" : row.GetString(row.GetOrdinal("EventName"))),
                    EventSource     = row.GetByte(row.GetOrdinal("EventSource")),
                    SourceName      = (row.IsDBNull(row.GetOrdinal("SourceName")) ? "" : row.GetString(row.GetOrdinal("SourceName"))),
                    EventData       = (row.IsDBNull(row.GetOrdinal("EventData")) ? "" : row.GetString(row.GetOrdinal("EventData"))),
                    UserName        = (row.IsDBNull(row.GetOrdinal("UserName")) ? "" : row.GetString(row.GetOrdinal("UserName")))
                };

                // We need to figure out when we have logged a particular audit record
                // to the Splunk database.  We do this with the query time and the 
                // checksums of the records in that query time - this code block does
                // this for us.
                if (oAuditRecord.Occurred.Ticks > LastQuery.Ticks)
                {
                    LastQuery = oAuditRecord.Occurred;
                    CheckSums.Clear();
                }
                if (oAuditRecord.Occurred.Ticks == LastQuery.Ticks)
                {
                    CheckSums.Add(oAuditRecord.CheckSum);
                }

                // Add the last audit record to the list to be returned.
                oAuditRecords.Add(oAuditRecord);
            }

            return oAuditRecords;
        }

        /// <summary>
        /// Returns a connection to this database, with the AuditData database connected
        /// </summary>
        /// <returns>The SqlConnection object</returns>
        private SqlConnection GetConnection()
        {
            SqlConnection connection = new SqlConnection(DatabaseConnectionString);
            connection.Open();
            return connection;
        }

        /// <summary>
        /// Create the query string necessary to get the latest Audit rows
        /// </summary>
        /// <returns>the query string</returns>
        private string GetQueryString()
        {
            string baseQuery = @"SELECT 
                CHECKSUM(*) AS CheckSum,
	            [AuditData].[SiteId], 
	            [AuditData].[ItemId], 
	            [AuditData].[ItemType], 
	            [AuditData].[UserId],
	            [AuditData].[DocLocation],
	            [AuditData].[LocationType],
	            [AuditData].[Occurred],
	            [AuditData].[Event],
	            [AuditData].[EventName],
	            [AuditData].[EventSource],
	            [AuditData].[SourceName],
	            [AuditData].[EventData],
	            [UserInfo].[tp_Login] AS UserName
	            FROM [dbo].[AuditData], [dbo].[UserInfo]
	            WHERE ([AuditData].[UserId] = [UserInfo].[tp_ID] AND [AuditData].[SiteId] = [UserInfo].[tp_SiteId])";
            StringBuilder query = new StringBuilder(baseQuery);

            // If we have a last query time, then use that
            if (!LastQuery.Equals(DateTime.MinValue))
            {
                query.AppendFormat(" AND ([AuditData].[Occurred] >= '{0}')", LastQuery.ToString("yyyy-MM-dd HH:mm:ss"));
            }

            // If we have any hashes, then use those
            if (CheckSums.Count > 0)
            {
                List<string> orField = new List<string>();
                foreach (int hash in CheckSums)
                {
                    orField.Add(string.Format("CHECKSUM(*) != {0}", hash));
                }
                query.AppendFormat(" AND ({0})", string.Join(" OR ", orField.ToArray()));
            }

            return query.ToString();
        }

        /// <summary>
        /// Work out what the checkpoint file for this database object is.
        /// </summary>
        /// <returns>The name of the checkpoint file</returns>
        private string CheckpointFile()
        {
            string sFileName = Id.ToString() + ".txt";
            return Path.Combine(CheckpointDirectory, sFileName);
        }

        /// <summary>
        /// Determine if the specified table exists
        /// </summary>
        /// <param name="tableName">table name</param>
        /// <returns>true if the table exists</returns>
        public bool TableExists(string tableName)
        {
            bool exists;

            SystemLogger.Write(LogLevel.Debug, string.Format("Checking to see if table {0} exists in database {1}", tableName, DatabaseConnectionString));
            SqlConnection connection = GetConnection();
            try
            {
                string query = string.Format("SELECT 1 FROM {0} WHERE 1=0", tableName);
                SystemLogger.Write(LogLevel.Debug, string.Format("Query = {0}", query));
                SqlCommand command = new SqlCommand(query, connection);
                command.ExecuteNonQuery();
                exists = true;
            }
            catch
            {
                exists = false;
            }

            return exists;
        }
    }
}
