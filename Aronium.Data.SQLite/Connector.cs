﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Data.SQLite;
using System.Linq;

namespace Aronium.Data.SQLite
{
    public class Connector : IDisposable
    {
        #region - Fields -

        private static readonly string CHECK_TABLE_EXISTS = "SELECT name FROM sqlite_master WHERE type='table' AND name=:TableName;";
        private static readonly string LAST_ROW_ID = "SELECT last_insert_rowid()";

        private string _dataFile;
        private string _connectionString;

        #endregion

        #region - Constructors -

        /// <summary>
        /// Initializes new instance of Connector class with specified data file.
        /// </summary>
        /// <param name="dataFile">SQLite database file path.</param>
        public Connector(string dataFile)
        {
            if (dataFile == null)
            {
                throw new ArgumentNullException("dataFile");
            }

            this.DataFile = dataFile;
        }

        #endregion

        #region - Properties -

        /// <summary>
        /// Gets database file name.
        /// <para>Property will be set upon successfull connect.</para>
        /// </summary>
        public string DataFile
        {
            get { return _dataFile; }
            private set
            {
                _dataFile = value;
            }
        }

        /// <summary>
        /// Gets or sets connection string.
        /// </summary>
        public string ConnectionString
        {
            get
            {
                if (string.IsNullOrEmpty(_connectionString))
                {
                    _connectionString = string.Format("data source={0};foreign keys=true;", this.DataFile);
                }
                return _connectionString;
            }
            set { _connectionString = value; }
        }

        #endregion

        #region - Private methods -

        private void PrepareCommandParameters(SQLiteCommand command, IEnumerable<SQLiteQueryParameter> args)
        {
            if (args != null && args.Any())
            {
                foreach (SQLiteQueryParameter parameter in args)
                {
                    if (parameter.Value != null && !(parameter.Value is string) && typeof(IEnumerable).IsAssignableFrom(parameter.Value.GetType()))
                    {
                        var parameterName = parameter.Name.Replace("@", string.Empty);

                        string replacement = string.Join(",", ((IEnumerable)parameter.Value).Cast<object>().Select((value, pos) => string.Format("@{0}__{1}", parameterName, pos)));

                        // Replace original command text with parametrized query
                        command.CommandText = command.CommandText.Replace(string.Format("@{0}", parameterName), replacement);

                        command.Parameters.AddRange(((IEnumerable)parameter.Value).Cast<object>().Select((value, pos) => new SQLiteParameter(string.Format("@{0}__{1}", parameterName, pos), value ?? DBNull.Value)).ToArray());
                    }
                    else
                    {
                        command.Parameters.Add(new SQLiteParameter(parameter.Name, parameter.Value ?? DBNull.Value));
                    }
                }
            }
        }

        #endregion

        #region - Public methods -

        /// <summary>
        /// Attempts to connect to database.
        /// </summary>
        /// <param name="fileName">Database file name.</param>
        /// <returns>True if connected successfully.</returns>
        public bool Connect(string fileName)
        {
            using (var connection = new SQLiteConnection(string.Format("Data Source={0};Version=3;", fileName)))
            {
                connection.Open();

                connection.Close();

                DataFile = fileName;

                return true;
            }
        }

        /// <summary>
        /// Executes query against in current database.
        /// </summary>
        /// <param name="query">Command text.</param>
        /// <param name="args">Query parameters.</param>
        /// <returns>Number of affected rows.</returns>
        public int Execute(string query, IEnumerable<SQLiteQueryParameter> args = null)
        {
            using (var connection = new SQLiteConnection(ConnectionString))
            {
                connection.Open();

                return Execute(query, args, connection);
            }
        }

        /// <summary>
        /// Executes query against in current database.
        /// </summary>
        /// <param name="query">Command text.</param>
        /// <param name="args">Query parameters.</param>
        /// <param name="connection">SQLiteConnection instance to use.</param>
        /// <returns>Number of affected rows.</returns>
        public int Execute(string query, IEnumerable<SQLiteQueryParameter> args, SQLiteConnection connection)
        {
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = query;

                PrepareCommandParameters(command, args);

                return command.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Executes query against in current database and assign row id to specified out parameter.
        /// </summary>
        /// <param name="query">Command text.</param>
        /// <param name="args">Query parameters.</param>
        /// <param name="rowId">Value to assing last insert row id to.</param>
        /// <returns>Number of affected rows.</returns>
        public int Execute(string query, IEnumerable<SQLiteQueryParameter> args, out long rowId)
        {
            using (var connection = new SQLiteConnection(ConnectionString))
            {
                connection.Open();

                return Execute(query, args, connection, out rowId);
            }
        }

        /// <summary>
        /// Executes query against in current database and assign row id to specified out parameter.
        /// </summary>
        /// <param name="query">Command text.</param>
        /// <param name="args">Query parameters.</param>
        /// <param name="connection">SQLiteConnection instance to use.</param>
        /// <param name="rowId">Value to assing last insert row id to.</param>
        /// <returns>Number of affected rows.</returns>
        public int Execute(string query, IEnumerable<SQLiteQueryParameter> args, SQLiteConnection connection, out long rowId)
        {
            using (SQLiteCommand command = connection.CreateCommand())
            {
                command.CommandText = query;

                PrepareCommandParameters(command, args);

                var rowsAffected = command.ExecuteNonQuery();

                #region " Assign ROWID "

                command.CommandText = LAST_ROW_ID;

                if (command.Parameters != null)
                    command.Parameters.Clear();

                using (SQLiteDataReader reader = command.ExecuteReader(CommandBehavior.SingleResult))
                {
                    reader.Read();

                    rowId = (long)reader[0];

                    reader.Close();
                }

                #endregion

                return rowsAffected;
            }
        }

        /// <summary>
        /// Executes prepared commands in transaction.
        /// </summary>
        /// <param name="commands">Commands to execute in single transaction.</param>
        public void Execute(IEnumerable<PreparedCommand> commands)
        {
            using (var connection = new SQLiteConnection(ConnectionString))
            {
                connection.Open();

                using (var transaction = connection.BeginTransaction())
                {
                    try
                    {
                        foreach (var preparedCommand in commands)
                        {
                            using (SQLiteCommand command = connection.CreateCommand())
                            {
                                command.CommandText = preparedCommand.CommandText;

                                PrepareCommandParameters(command, preparedCommand.QueryArguments);

                                command.ExecuteNonQuery();
                            }
                        }

                        transaction.Commit();
                    }
                    catch (Exception ex)
                    {
                        transaction.Rollback();

                        Console.Error.WriteLine(ex);

                        throw;
                    }
                }
            }
        }

        /// <summary>
        /// Gets entity instance.
        /// </summary>
        /// <typeparam name="T">Type of object to create</typeparam>
        /// <param name="query">Sql Query</param>
        /// <param name="args">Sql Parameters</param>
        /// <returns>Entity instance.</returns>
        /// <remarks>Instance properties are populated from database record using reflection for the given type.</remarks>
        public T SelectEntity<T>(string query, IEnumerable<SQLiteQueryParameter> args = null) where T : class, new()
        {
            T entity = null;

            using (var connection = new SQLiteConnection(ConnectionString))
            {
                connection.Open();

                using (SQLiteCommand command = connection.CreateCommand())
                {
                    command.CommandText = query;

                    PrepareCommandParameters(command, args);

                    using (SQLiteDataReader reader = command.ExecuteReader())
                    {
                        reader.Read();

                        if (reader.HasRows)
                        {
                            entity = new T();
                            var type = typeof(T);

                            for (int i = 0; i < reader.FieldCount; i++)
                            {
                                var val = reader[i];

                                var property = type.GetProperty(reader.GetName(i));

                                if (property != null)
                                {
                                    // SQLite stores primary keys as Int64
                                    // Standard implementation uses int as id field, which must be converted from Int64 to Int32
                                    if (val is long && property.PropertyType == typeof(Int32))
                                    {
                                        val = Convert.ToInt32(val);
                                    }
                                    else if (val is long && property.PropertyType == typeof(Decimal))
                                    {
                                        val = Convert.ToDecimal(val);
                                    }
                                    else if (val is string && property.PropertyType == typeof(Guid))
                                    {
                                        val = new Guid((string)val);
                                    }

                                    property.SetValue(entity, val == Convert.DBNull ? null : val, null);
                                }
                            }
                        }

                        reader.Close();
                    }
                }
            }

            return entity;
        }

        /// <summary>
        /// Execute reader and create instance of provided type using IRowMapper interface.
        /// </summary>
        /// <typeparam name="T">Type of object to create.</typeparam>
        /// <param name="args">Sql Parameters.</param>
        /// <param name="query">Sql Query.</param>
        /// <param name="rowMapper">IRowMapper used to map object instance from reader.</param>
        /// <param name="isStoredProcedure">Indicating whether query type is stored procedure.</param>
        /// <returns>Instance of object type.</returns>
        public T SelectValue<T>(string query, IEnumerable<SQLiteQueryParameter> args = null, IRowMapper<T> rowMapper = null)
        {
            object obj = null;

            using (var connection = new SQLiteConnection(ConnectionString))
            {
                connection.Open();

                using (SQLiteCommand command = connection.CreateCommand())
                {
                    command.CommandText = query;

                    PrepareCommandParameters(command, args);

                    using (SQLiteDataReader reader = command.ExecuteReader(CommandBehavior.SingleResult))
                    {
                        reader.Read();

                        if (reader.HasRows)
                        {
                            if (rowMapper != null)
                            {
                                obj = rowMapper.Map(reader);
                            }
                            else
                            {
                                // Used for primitive types
                                obj = reader[0];
                            }
                        }

                        reader.Close();
                    }
                }
            }

            if (obj == null)
                return default(T);

            return (T)obj;
        }

        /// <summary>
        /// Gets list of entities.
        /// </summary>
        /// <typeparam name="T">Type of object to create</typeparam>
        /// <param name="query">Sql Query</param>
        /// <param name="args">Sql query parameters</param>
        /// <returns>List of entities.</returns>
        /// <remarks>Instance properties are populated from database record using reflection for the speecified type.</remarks>
        public IEnumerable<T> Select<T>(string query, IEnumerable<SQLiteQueryParameter> args = null) where T : class, new()
        {
            using (var connection = new SQLiteConnection(ConnectionString))
            {
                var type = typeof(T);

                connection.Open();

                using (SQLiteCommand command = connection.CreateCommand())
                {
                    command.CommandText = query;

                    PrepareCommandParameters(command, args);

                    using (SQLiteDataReader reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            T entity = new T();

                            for (int i = 0; i < reader.FieldCount; i++)
                            {
                                var val = reader[i];

                                var property = type.GetProperty(reader.GetName(i));

                                if (property != null)
                                {
                                    // SQLite stores primary keys as Int64
                                    // Standard implementation uses int as id field, which must be converted from Int64 to Int32
                                    if (val is long && property.PropertyType == typeof(Int32))
                                    {
                                        val = Convert.ToInt32(val);
                                    }

                                    if (property.PropertyType == typeof(Decimal))
                                    {
                                        val = Convert.ToDecimal(val);
                                    }

                                    if (val is string && property.PropertyType == typeof(Guid))
                                    {
                                        val = new Guid((string)val);
                                    }

                                    if (val is string && property.PropertyType == typeof(DateTime))
                                    {
                                        val = DateTime.Parse((string)val);
                                    }

                                    property.SetValue(entity, val == Convert.DBNull ? null : val, null);
                                }
                            }

                            yield return entity;
                        }

                        reader.Close();
                    }
                }
            }
        }

        /// <summary>
        /// Execute reader and create list of provided type using IRowMapper interface.
        /// </summary>
        /// <typeparam name="T">Type of object to create.</typeparam>
        /// <param name="query">Sql Query.</param>
        /// <param name="args">Sql query parameters.</param>
        /// <param name="rowMapper">IRowMapper used to map object instance from reader.</param>
        /// <returns>List of provided object type.</returns>
        public IEnumerable<T> Select<T>(string query, IEnumerable<SQLiteQueryParameter> args, IRowMapper<T> rowMapper)
        {
            using (var connection = new SQLiteConnection(ConnectionString))
            {
                connection.Open();

                using (SQLiteCommand command = connection.CreateCommand())
                {
                    command.CommandText = query;

                    PrepareCommandParameters(command, args);

                    using (SQLiteDataReader reader = command.ExecuteReader())
                    {
                        var isNullable = Nullable.GetUnderlyingType(typeof(T)) != null;

                        while (reader.Read())
                        {
                            if (rowMapper != null)
                            {
                                yield return rowMapper.Map(reader);
                            }
                            else
                            {
                                // Check for null values and return default instance of T (should be nullable)
                                // If not checked for NULL values, conversion will fail, resulting in InvalidCastException being thrown
                                if (isNullable && reader[0] == Convert.DBNull)
                                {
                                    yield return default(T);
                                }
                                else
                                    yield return (T)reader[0];
                            }
                        }

                        reader.Close();
                    }
                }
            }
        }

        /// <summary>
        /// Execute reader and create list of provided type using IRowMapper interface.
        /// </summary>
        /// <typeparam name="T">Type of object to create.</typeparam>
        /// <param name="query">Sql Query.</param>
        /// <param name="args">Sql Parameters.</param>
        /// <param name="extractor">IDataExtractor used to map object instance from reader.</param>
        /// <param name="isStoredProcedure">indicating if query type is stored procedure.</param>
        /// <returns>List of provided object type.</returns>
        public IEnumerable<T> Select<T>(string query, IEnumerable<SQLiteQueryParameter> args, IDataExtractor<T> extractor)
        {
            using (var connection = new SQLiteConnection(ConnectionString))
            {
                connection.Open();

                using (SQLiteCommand command = connection.CreateCommand())
                {
                    command.CommandText = query;

                    PrepareCommandParameters(command, args);

                    IEnumerable<T> result;

                    using (SQLiteDataReader reader = command.ExecuteReader())
                    {
                        result = extractor.Extract(reader);

                        reader.Close();
                    }

                    return result;
                }
            }
        }

        /// <summary>
        /// Gets a value indicating whether table with specified name exists.
        /// </summary>
        /// <param name="tableName">Table name.</param>
        /// <returns>True if table exists, otherwise false.</returns>
        public bool TableExists(string tableName)
        {
            bool exists = false;

            using (var connection = new SQLiteConnection(ConnectionString))
            {
                connection.Open();

                using (SQLiteCommand command = connection.CreateCommand())
                {
                    command.CommandText = CHECK_TABLE_EXISTS;

                    command.Parameters.AddWithValue("TableName", tableName);

                    using (SQLiteDataReader reader = command.ExecuteReader())
                    {
                        reader.Read();

                        exists = reader.HasRows;

                        reader.Close();
                    }
                }
            }

            return exists;
        }

        /// <summary>
        /// Disposes this instance.
        /// </summary>
        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
