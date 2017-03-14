﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FirebirdSql.Data.FirebirdClient;
using System.Reflection;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Data.Common;

namespace Puch.FirebirdHelper
{
    public abstract class FirebirdTable<T> where T : FirebirdRow, new()
    {
        static readonly PropertyInfo[] _fields = typeof(T).GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Where(p => p.GetCustomAttributes(typeof(ColumnAttribute), true).Any()).ToArray();

        protected List<T> Select(string sqlQuery, params object[] parameters) { return Select(sqlQuery, null, parameters); }
        protected List<T> Select(string sqlQuery, DbTransaction transaction, params object[] parameters)
        {
            try
            {
                var rows = new List<T>();
                FbCommand command = Connector.GetCommand(sqlQuery, (FbTransaction)transaction, parameters);
                using (FbDataReader reader = command.ExecuteReader())
                {
                    string[] fieldNames = new string[reader.FieldCount];
                    for (int i = 0; i < reader.FieldCount; i++)
                        fieldNames[i] = reader.GetName(i);
                    while (reader.Read())
                    {
                        T record = _read(reader, fieldNames);
                        rows.Add(record);
                    }
                }
                return rows;
            }
            catch (Exception e)
            {
                Debug.WriteLine(e);
                return null;
            }
        }

        protected T _read(FbDataReader reader, string[] fieldNames)
        {
            T row = New();
            row.IsDbReading = true;
            try
            {
                _readFields(reader, _fields, row, fieldNames);
                return row;
            }
            finally
            {
                row.IsDbReading = false;
            }
        }

        protected void _readFields(FbDataReader reader, IEnumerable<PropertyInfo> fields, T row, string[] fieldNames = null)
        {
            if (fieldNames == null)
            {
                fieldNames = new string[reader.FieldCount];
                for (int i = 0; i < reader.FieldCount; i++)
                    fieldNames[i] = reader.GetName(i);
            }
            foreach (PropertyInfo field in fields)
            {
                var cna = field.GetCustomAttributes(typeof(ColumnAttribute), false).FirstOrDefault();
                string fieldName = (cna == null) ? field.Name.ToUpperInvariant() : ((ColumnAttribute)cna).Name;
                if (fieldNames.Contains(fieldName))
                {
                    object dbValue = reader.GetValue(reader.GetOrdinal(fieldName));
                    object instanceValue = field.GetValue(row, null);
                    lock (row.FieldsPreviousValue)
                    {
                        if (!dbValue.Equals(instanceValue))
                            if (row.FieldsPreviousValue.ContainsKey(field))  // if user modified this field
                                row.FieldsPreviousValue[field] = (dbValue == DBNull.Value) ? null : dbValue;
                            else
                                field.SetValue(row, (dbValue == DBNull.Value) ? null : dbValue, null);
                    }
                }
            }
        }
        

        public virtual T New()
        {
            return new T() { Table = this };
        }

        private void _refreshAutoUpdatedFields(T row, FbTransaction transaction, bool onUpdate, bool onInsert)
        {
            _refreshFields(row, _fields.Where(f => 
                {
                    if (onUpdate)
                        return f.GetCustomAttributes(typeof(DatabaseAutoUpdatedAttribute), true).Where(a => ((DatabaseAutoUpdatedAttribute) a).OnUpdate).Any();
                    if (onInsert)
                        return f.GetCustomAttributes(typeof(DatabaseAutoUpdatedAttribute), true).Where(a => ((DatabaseAutoUpdatedAttribute)a).OnInsert).Any();
                    return false;
                }), transaction);
        }

        private void _refreshFields(T row, IEnumerable<PropertyInfo> fields, FbTransaction transaction)
        {
            ColumnAttribute idAttribute = (ColumnAttribute)row.GetType().GetProperty("Id").GetCustomAttributes(typeof(ColumnAttribute), false).First();
            if (fields.Count() > 0)
            {
                row.IsDbReading = true;
                try
                {
                    StringBuilder sql = new StringBuilder("select ");
                    string[] fieldNames = new string[fields.Count()];
                    for (int i = 0; i < fields.Count(); i++)
                    {
                        ColumnAttribute an = (ColumnAttribute)(fields.ElementAt(i).GetCustomAttributes(typeof(ColumnAttribute), true).FirstOrDefault());
                        string fieldName = an == null ? fields.ElementAt(i).Name.ToUpperInvariant() : an.Name;
                        sql.Append(fieldName);
                        fieldNames[i] = fieldName;
                        if (i < fields.Count() - 1)
                            sql.Append(", ");
                    }
                    sql.Append(" from ")
                        .Append(((TableNameAttribute)this.GetType().GetCustomAttributes(typeof(TableNameAttribute), true).First()).Name)
                        .Append(" where ")
                        .Append(idAttribute.Name)
                        .Append("=")
                        .Append(row.Id);
                    FbCommand command = transaction == null ? new FbCommand(sql.ToString(), Connector.Connection) : new FbCommand(sql.ToString(), Connector.Connection, transaction);
                    using (FbDataReader reader = command.ExecuteReader(System.Data.CommandBehavior.SingleRow))
                    {
                        if (reader.Read())
                            _readFields(reader, fields, row);
                    }
                }
                finally
                {
                    row.IsDbReading = false;
                }
            }
        }

        protected void RefreshRow(T row, DbTransaction transaction)
        {
            _refreshFields(row, _fields.Where(f => !f.GetCustomAttributes(typeof(JoinFieldAttribute), true).Any()), (FbTransaction)transaction);
        }
    }
}
