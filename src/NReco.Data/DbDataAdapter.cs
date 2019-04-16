﻿#region License
/*
 * NReco Data library (http://www.nrecosite.com/)
 * Copyright 2016 Vitaliy Fedorchenko
 * Distributed under the MIT license
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */
#endregion

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Data.Common;
using System.Data;

namespace NReco.Data {

	/// <summary>
	/// Data adapter between database and application data models. Implements select, insert, update and delete operations.
	/// </summary>
	public partial class DbDataAdapter : IRecordSetAdapter, IDisposable {

		/// <summary>
		/// Gets <see cref="IDbConnection"/> associated with this data adapter.
		/// </summary>
		public IDbConnection Connection { get; private set; }

		/// <summary>
		/// Gets <see cref="IDbCommandBuilder"/> associated with this data adapter.
		/// </summary>
		public IDbCommandBuilder CommandBuilder { get; private set; }

		/// <summary>
		/// Gets or sets <see cref="IDbTransaction"/> initiated for the <see cref="Connection"/>.
		/// </summary>
		public IDbTransaction Transaction { get; set; }

		/// <summary>
		/// Gets or sets flag that determines whether query record offset is applied during reading query results.
		/// </summary>
		public bool ApplyOffset { get; set; } = true;

		/// <summary>
		/// Initializes a new instance of the DbDataAdapter.
		/// </summary>
		/// <param name="connection">database connection instance</param>
		/// <param name="cmdBuilder">command builder instance</param>
		public DbDataAdapter(IDbConnection connection, IDbCommandBuilder cmdBuilder) {
			Connection = connection;
			CommandBuilder = cmdBuilder;
		}

		private void SetupCmd(IDbCommand cmd) {
			cmd.Connection = Connection;
			if (Transaction!=null)
				cmd.Transaction = Transaction;
		}

		/// <summary>
		/// Returns prepared select query. 
		/// </summary>
		/// <param name="q">query to execute</param>
		/// <returns>prepared select query</returns>
		public SelectQuery Select(Query q) {
			return new SelectQueryByQuery(this, q);
		}

		/// <summary>
		/// Creates a <see cref="SelectQuery"/> based on a raw SQL query.
		/// </summary>
		/// <param name="sql">The raw SQL query.</param>
		/// <param name="parameters">The values to be assigned to parameters.</param>
		/// <returns>prepared select query</returns>
		/// <remarks>Semantics of this method is similar to EF Core DbSet.FromSql. Any parameter values you supply will automatically be converted to a DbParameter:
		/// <code>dbAdapter.Select("SELECT * FROM [dbo].[SearchBlogs]({0})", userSuppliedSearchTerm).ToRecordSet()</code>.
		/// <para>You can also construct a DbParameter and supply it to as a parameter value. This allows you to use named
        /// parameters in the SQL query string - 
		/// <code>dbAdapter.Select("SELECT * FROM [dbo].[SearchBlogs]({@searchTerm})", new SqlParameter("@searchTerm", userSuppliedSearchTerm)).ToRecordSet()</code>
		/// </para>
		/// </remarks>
		public SelectQuery Select(RawSqlString sql, params object[] parameters) {
			return new SelectQueryBySql(this, sql.Format, parameters);
		}

#if NET_STANDARD
		/// <summary>
		/// Creates a <see cref="SelectQuery"/> based on an interpolated string representing a SQL query.
		/// </summary>
		/// <remarks>
		/// Semantics of this method is similar to EF Core DbSet.FromSql:
		/// <code>
		/// dbAdapter.Select($"SELECT * FROM Users WHERE id={userId}").Single&lt;User&gt;();
		/// </code>
		/// All parameters will automatically be converted to a DbParameter.
		/// </remarks>
		public SelectQuery Select(FormattableString sql) {
			return new SelectQueryBySql(this, sql.Format, sql.GetArguments());
		}
#endif

		/// <summary>
		/// Creates a <see cref="SelectQuery"/> based on specified <see cref="IDbCommand"/> instance.
		/// </summary>
		/// <param name="cmd">custom command</param>
		/// <returns>prepared select query</returns>
		/// <remarks>This method allows to execute custom SQL command and map results like for usual SELECT command.</remarks>
		public SelectQuery Select(IDbCommand cmd) {
			return new SelectQueryByCmd(this, cmd);
		}

		int InsertInternal(string tableName, IEnumerable<KeyValuePair<string,IQueryValue>> data) {
			if (tableName==null)
				throw new ArgumentNullException($"{nameof(tableName)}");
			return ExecuteNonQuery( CommandBuilder.GetInsertCommand(tableName, data) );
		}

		Task<int> InsertInternalAsync(string tableName, IEnumerable<KeyValuePair<string,IQueryValue>> data) {
			if (tableName==null)
				throw new ArgumentNullException($"{nameof(tableName)}");
			return ExecuteNonQueryAsync( CommandBuilder.GetInsertCommand(tableName, data), CancellationToken.None );
		}

		DataMapper.ColumnMapping FindAutoIncrementColumn(object pocoModel) {
			var schema = DataMapper.Instance.GetSchema(pocoModel.GetType());
			foreach (var colMapping in schema.Columns)
				if (colMapping.IsIdentity)
					return colMapping;
			return null;
		}
		
		/// <summary>
		/// Executes INSERT statement generated by specified table name and dictionary values.
		/// </summary>
		/// <param name="tableName">table name</param>
		/// <param name="data">dictonary with new record data (column -> value)</param>
		/// <returns>Number of inserted data records.</returns>
		public int Insert(string tableName, IDictionary<string,object> data) {
			if (data==null)
				throw new ArgumentNullException($"{nameof(data)}");
			return InsertInternal(tableName, DataHelper.GetChangeset(data) );
		}

		/// <summary>
		/// Asynchronously executes INSERT statement generated by specified table name and dictionary values.
		/// </summary>
		public Task<int> InsertAsync(string tableName, IDictionary<string,object> data) {
			if (data==null)
				throw new ArgumentNullException($"{nameof(data)}");
			return InsertInternalAsync(tableName, DataHelper.GetChangeset(data) );
		}

		/// <summary>
		/// Executes INSERT statement generated by specified table name and annotated POCO model.
		/// </summary>
		/// <param name="tableName">table name</param>
		/// <param name="pocoModel">POCO model with public properties that match table columns.</param>
		/// <returns>Number of inserted data records.</returns>
		public int Insert(string tableName, object pocoModel) {
			if (pocoModel==null)
				throw new ArgumentNullException($"{nameof(pocoModel)}");
			int affected = 0;
			DataHelper.EnsureConnectionOpen(Connection, () => {
				affected = InsertInternal(tableName, DataHelper.GetChangeset( pocoModel, null) );
				var autoIncrementCol = FindAutoIncrementColumn(pocoModel);
				if (autoIncrementCol!=null) {
					var insertedId = CommandBuilder.DbFactory.GetInsertId(Connection, Transaction);
					if (insertedId!=null)
						autoIncrementCol.SetValue(pocoModel, insertedId);
				}
			});
			return affected;
		}

		/// <summary>
		/// Asynchronously executes INSERT statement generated by specified table name and POCO model.
		/// </summary>
		public async Task<int> InsertAsync(string tableName, object pocoModel) {
			if (pocoModel==null)
				throw new ArgumentNullException($"{nameof(pocoModel)}");
			if (tableName==null)
				throw new ArgumentNullException($"{nameof(tableName)}");

			CancellationToken cancel = CancellationToken.None;
			var isClosedConn = Connection.State == ConnectionState.Closed;
			if (isClosedConn) {
				await Connection.OpenAsync(cancel).ConfigureAwait(false);
			}
			int affected = 0;
			try {
				affected = await InsertInternalAsync(tableName, DataHelper.GetChangeset( pocoModel, null) ).ConfigureAwait(false);
				var autoIncrementCol = FindAutoIncrementColumn(pocoModel);
				if (autoIncrementCol!=null) {
					var insertedId = await CommandBuilder.DbFactory.GetInsertIdAsync(Connection, Transaction, cancel).ConfigureAwait(false);
					if (insertedId!=null)
						autoIncrementCol.SetValue(pocoModel, insertedId);
				}
			} finally {
				if (isClosedConn)
					Connection.Close();
			}			

			return affected;
		}

		/// <summary>
		/// Executes INSERT statement generated by annotated POCO model.
		/// </summary>
		/// <param name="pocoModel">POCO model (possibly annotated).</param>
		/// <returns>Number of inserted data records.</returns>
		public int Insert(object pocoModel) {
			if (pocoModel==null)
				throw new ArgumentNullException($"{nameof(pocoModel)}");
			var schema = DataMapper.Instance.GetSchema(pocoModel.GetType());
			return Insert(schema.TableName, pocoModel);			
		}

		/// <summary>
		/// Asynchronously executes INSERT statement generated by annotated POCO model.
		/// </summary>
		public Task<int> InsertAsync(object pocoModel) {
			if (pocoModel==null)
				throw new ArgumentNullException($"{nameof(pocoModel)}");
			var schema = DataMapper.Instance.GetSchema(pocoModel.GetType());
			return InsertAsync(schema.TableName, pocoModel);				
		}

		int UpdateInternal(Query query, IEnumerable<KeyValuePair<string,IQueryValue>> data) {
			if (query==null)
				throw new ArgumentNullException($"{nameof(query)}");
			return ExecuteNonQuery( CommandBuilder.GetUpdateCommand(query, data) );
		}

		Task<int> UpdateInternalAsync(Query query, IEnumerable<KeyValuePair<string,IQueryValue>> data) {
			if (query==null)
				throw new ArgumentNullException($"{nameof(query)}");
			return ExecuteNonQueryAsync( CommandBuilder.GetUpdateCommand(query, data), CancellationToken.None );
		}
		
		/// <summary>
		/// Executes UPDATE statement generated by specified <see cref="Query"/> and dictionary values.
		/// </summary>
		/// <param name="query">query that determines data records to update.</param>
		/// <param name="data">dictonary with changeset data (column -> value)</param>
		/// <returns>Number of updated data records.</returns>
		public int Update(Query query, IDictionary<string,object> data) {
			if (data==null)
				throw new ArgumentNullException($"{nameof(data)}");
			return UpdateInternal(query, DataHelper.GetChangeset(data) );
		}

		/// <summary>
		/// Asynchronously executes UPDATE statement generated by specified <see cref="Query"/> and dictionary values.
		/// </summary>
		public Task<int> UpdateAsync(Query query, IDictionary<string,object> data) {
			if (data==null)
				throw new ArgumentNullException($"{nameof(data)}");
			return UpdateInternalAsync(query, DataHelper.GetChangeset(data) );
		}

		/// <summary>
		/// Executes UPDATE statement generated by specified <see cref="Query"/> and POCO model.
		/// </summary>
		/// <param name="query">query that determines data records to update.</param>
		/// <param name="pocoModel">POCO model with public properties that match table columns.</param>
		/// <returns>Number of updated data records.</returns>
		public int Update(Query query, object pocoModel) {
			if (pocoModel==null)
				throw new ArgumentNullException($"{nameof(pocoModel)}");
			return UpdateInternal(query, DataHelper.GetChangeset( pocoModel, null) );
		}

		/// <summary>
		/// Asynchronously executes UPDATE statement generated by specified <see cref="Query"/> and POCO model.
		/// </summary>
		public Task<int> UpdateAsync(Query query, object pocoModel) {
			if (pocoModel==null)
				throw new ArgumentNullException($"{nameof(pocoModel)}");
			return UpdateInternalAsync(query, DataHelper.GetChangeset( pocoModel, null) );
		}

		/// <summary>
		/// Executes UPDATE statement generated by annotated POCO model.
		/// </summary>
		/// <param name="pocoModel">annotated POCO model (key should be defined).</param>	
		/// <returns>Number of updated data records.</returns>
		public int Update(object pocoModel) {
			if (pocoModel==null)
				throw new ArgumentNullException($"{nameof(pocoModel)}");
			return Update( GetQueryByKey(pocoModel), pocoModel);
		}

		/// <summary>
		/// Asynchronously executes UPDATE statement generated by annotated POCO model.
		/// </summary>
		public Task<int> UpdateAsync(object pocoModel) {
			if (pocoModel==null)
				throw new ArgumentNullException($"{nameof(pocoModel)}");
			return UpdateAsync( GetQueryByKey(pocoModel), pocoModel);
		}

		Query GetQueryByKey(object pocoModel) {
			var schema = DataMapper.Instance.GetSchema(pocoModel.GetType());
			if (schema.Key.Length==0)
				throw new ArgumentException("Specified object doesn't have properties annotated with KeyAttribute.");
			var q = new Query( new QTable(schema.TableName, null) );
			var andGroup = QGroupNode.And();
			q.Condition = andGroup;
			for (int i=0; i<schema.Key.Length; i++) {
				var keyCol = schema.Key[i];
				if (keyCol.GetVal!=null)
					andGroup.Nodes.Add( (QField)keyCol.ColumnName == new QConst(keyCol.GetVal(pocoModel) ) );
			}
			return q;
		}

		void EnsurePrimaryKey(RecordSet recordSet) {
			if (recordSet.PrimaryKey==null || recordSet.PrimaryKey.Length==0)
				throw new NotSupportedException("Update operation can be performed only for RecordSet with PrimaryKey");
		}

		RecordSet IRecordSetAdapter.Select(Query q) {
			return Select(q).ToRecordSet();
		}

		Task<RecordSet> IRecordSetAdapter.SelectAsync(Query q) {
			return Select(q).ToRecordSetAsync();
		}

		/// <summary>
		/// Calls the respective INSERT, UPDATE, or DELETE statements for each inserted, updated, or deleted row in the specified <see cref="RecordSet"/>.
		/// </summary>
		/// <param name="tableName">The name of the source table.</param>
		/// <param name="recordSet"><see cref="RecordSet"/> to use to update the data source.</param>
		/// <returns>The number of rows successfully updated.</returns>
		/// <remarks>
		/// <para><see cref="RecordSet.PrimaryKey"/> should be set before calling <see cref="DbDataAdapter.Update(string, RecordSet)"/>.</para>
		/// When an application calls the Update method, <see cref="DbDataAdapter"/> examines the <see cref="RecordSet.Row.State"/> property,
		/// and executes the required INSERT, UPDATE, or DELETE statements iteratively for each row (based on the order of rows in RecordSet).
		/// </remarks>
		public int Update(string tableName, RecordSet recordSet) {
			EnsurePrimaryKey(recordSet);
			int affected = 0;
			using (var rsAdapter = new RecordSetAdapter(this, tableName, recordSet)) {
				affected = rsAdapter.Update();
			}
			recordSet.AcceptChanges();
			return affected;
		}

		/// <summary>
		/// An asynchronous version of <see cref="Update(string, RecordSet)"/> that calls the respective INSERT, UPDATE, or DELETE statements 
		/// for each added, updated, or deleted row in the specified <see cref="RecordSet"/>.
		/// </summary>
		/// <param name="tableName">The name of the source table.</param>
		/// <param name="recordSet"><see cref="RecordSet"/> to use to update the data source.</param>
		/// <param name="cancel">The cancellation instruction.</param>
		/// <returns>The number of rows successfully updated.</returns>
		public Task<int> UpdateAsync(string tableName, RecordSet recordSet) {
			return UpdateAsync(tableName, recordSet, CancellationToken.None); 
		}

		/// <summary>
		/// An asynchronous version of <see cref="Update(string, RecordSet)"/> that calls the respective INSERT, UPDATE, or DELETE statements 
		/// for each added, updated, or deleted row in the specified <see cref="RecordSet"/>.
		/// </summary>
		/// <param name="tableName">The name of the source table.</param>
		/// <param name="recordSet"><see cref="RecordSet"/> to use to update the data source.</param>
		/// <param name="cancel">The cancellation instruction.</param>
		/// <returns>The number of rows successfully updated.</returns>
		public async Task<int> UpdateAsync(string tableName, RecordSet recordSet, CancellationToken cancel) {
			EnsurePrimaryKey(recordSet);
			int affected = 0;
			using (var rsAdapter = new RecordSetAdapter(this, tableName, recordSet)) {
				affected = await rsAdapter.UpdateAsync(cancel).ConfigureAwait(false);
			}
			recordSet.AcceptChanges();
			return affected;		
		}


		/// <summary>
		/// Executes DELETE statement generated by specified <see cref="Query"/>.
		/// </summary>
		/// <param name="q">query that determines data records to delete.</param>
		/// <returns>Number of actually deleted records.</returns>
		public int Delete(Query q) {
			return ExecuteNonQuery( CommandBuilder.GetDeleteCommand(q) );
		}

		/// <summary>
		/// Executes DELETE statement generated by annotated POCO model.
		/// </summary>
		/// <param name="pocoModel">annotated POCO model (key should be defined).</param>	
		/// <returns>Number of actually deleted records.</returns>
		public int Delete(object pocoModel) {
			if (pocoModel==null)
				throw new ArgumentNullException($"{nameof(pocoModel)}");
			return Delete( GetQueryByKey(pocoModel) );
		}

		/// <summary>
		/// Asynchronously executes DELETE statement generated by annotated POCO model.
		/// </summary>
		public Task<int> DeleteAsync(object pocoModel) {
			if (pocoModel==null)
				throw new ArgumentNullException($"{nameof(pocoModel)}");
			return DeleteAsync(  GetQueryByKey(pocoModel) );			
		}

		/// <summary>
		/// Asynchronously executes DELETE statement generated by specified <see cref="Query"/>.
		/// </summary>
		public Task<int> DeleteAsync(Query q) {
			return DeleteAsync(q, CancellationToken.None);
		}

		/// <summary>
		/// Asynchronously executes DELETE statement generated by specified <see cref="Query"/>.
		/// </summary>
		public Task<int> DeleteAsync(Query q, CancellationToken cancel) {
			return ExecuteNonQueryAsync( CommandBuilder.GetDeleteCommand(q), cancel );
		}

		private int ExecuteNonQuery(IDbCommand cmd) {
			int affectedRecords = 0;
			SetupCmd(cmd);
			using (cmd) {
				DataHelper.EnsureConnectionOpen(Connection, () => {
					try {
						affectedRecords = cmd.ExecuteNonQuery();
					} catch (Exception ex) {
						throw new ExecuteDbCommandException(cmd, ex);
					}
				});
			}
			return affectedRecords;
		}

		private async Task<int> ExecuteNonQueryAsync(IDbCommand cmd, CancellationToken cancel) {
			int affected = 0;
			using (cmd) {
				SetupCmd(cmd);
				var isClosedConn = cmd.Connection.State == ConnectionState.Closed;
				if (isClosedConn) {
					await cmd.Connection.OpenAsync(cancel).ConfigureAwait(false);
				}
				try {
					affected = await cmd.ExecuteNonQueryAsync(cancel).ConfigureAwait(false);	
				} catch (Exception ex) {
					throw new ExecuteDbCommandException(cmd, ex);
				} finally {
					if (isClosedConn)
						cmd.Connection.Close();
				}
			}
			return affected;
		}

		public void Dispose() {
			Dispose(true);
		}

		protected virtual void Dispose(bool disposing) {
			if (disposing) {
				Connection = null;
				CommandBuilder = null;
				Transaction = null;
			}
		}

	}
}
