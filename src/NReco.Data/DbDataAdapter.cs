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
using System.Threading.Tasks;
using System.Data.Common;
using System.Data;

namespace NReco.Data {

	/// <summary>
	/// Data adapter between database and application data models. Implements select, insert, update and delete operations.
	/// </summary>
	public class DbDataAdapter {

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

		private void InitCmd(IDbCommand cmd) {
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
			var selectCmd = CommandBuilder.GetSelectCommand(q);
			InitCmd(selectCmd);
			return new SelectQuery(this, selectCmd, q, null);
		}

		/// <summary>
		/// Returns prepared select query with POCO-model fields mapping configuration. 
		/// </summary>
		/// <param name="q">query to execute</param>
		/// <returns>prepared select query</returns>
		public SelectQuery Select(Query q, IDictionary<string,string> fieldToPropertyMap) {
			var selectCmd = CommandBuilder.GetSelectCommand(q);
			InitCmd(selectCmd);
			return new SelectQuery(this, selectCmd, q, fieldToPropertyMap);
		}

		int InsertInternal(string tableName, IEnumerable<KeyValuePair<string,IQueryValue>> data) {
			var insertCmd = CommandBuilder.GetInsertCommand(tableName, data);
			InitCmd(insertCmd);
			return ExecuteNonQuery(insertCmd);
		}
		
		public int Insert(string tableName, IDictionary<string,object> data) {
			return InsertInternal(tableName, DataHelper.GetChangeset(data) );
		}

		public int Insert(string tableName, object pocoModel) {
			return InsertInternal(tableName, DataHelper.GetChangeset( pocoModel, null) );
		}

		public int Insert(string tableName, object pocoModel, IDictionary<string,string> propertyToFieldMap) {
			return InsertInternal(tableName, DataHelper.GetChangeset( pocoModel, propertyToFieldMap) );
		}

		
		int UpdateInternal(Query q, IEnumerable<KeyValuePair<string,IQueryValue>> data) {
			var updateCmd = CommandBuilder.GetUpdateCommand(q, data);
			InitCmd(updateCmd);
			return ExecuteNonQuery(updateCmd);
		}
		
		public int Update(Query q, IDictionary<string,object> data) {
			return UpdateInternal(q, DataHelper.GetChangeset(data) );
		}

		public int Update(Query q, object pocoModel) {
			return UpdateInternal(q, DataHelper.GetChangeset( pocoModel, null) );
		}

		public int Update(Query q, object pocoModel, IDictionary<string,string> propertyToFieldMap) {
			return UpdateInternal(q, DataHelper.GetChangeset( pocoModel, propertyToFieldMap) );
		}

		public int Update(string tableName, RecordSet recordSet) {
			if (recordSet.PrimaryKey==null || recordSet.PrimaryKey.Length==0)
				throw new NotSupportedException("Update operation can be performed only for RecordSet with PrimaryKey");
			var rsAdapter = new RecordSetAdapter(this, tableName, recordSet);
			var affected = rsAdapter.Update();
			recordSet.AcceptChanges();
			return affected;
		}

		public int Delete(Query q) {
			var deleteCmd = CommandBuilder.GetDeleteCommand(q);
			InitCmd(deleteCmd);
			return ExecuteNonQuery(deleteCmd);
		}

		private int ExecuteNonQuery(IDbCommand cmd) {
			int affectedRecords = 0;
			DataHelper.EnsureConnectionOpen(Connection, () => {
				affectedRecords = cmd.ExecuteNonQuery();
			});
			return affectedRecords;
		}

		internal class RecordSetAdapter {
			RecordSet RS;
			DbDataAdapter DbAdapter;
			string TableName;
			IDbCommand InsertCmd = null;
			IDbCommand UpdateCmd = null;
			IDbCommand DeleteCmd = null;
			RecordSet.Column[] setColumns;
			RecordSet.Column autoIncrementCol;

			internal RecordSetAdapter(DbDataAdapter dbAdapter, string tblName, RecordSet rs) {
				RS = rs;
				DbAdapter = dbAdapter;
				TableName = tblName;
				setColumns = RS.Columns.Where(c=>!c.AutoIncrement).ToArray();
				if (setColumns.Length!=RS.Columns.Length)
					autoIncrementCol = RS.Columns.Where(c=>c.AutoIncrement).FirstOrDefault();
			}

			IEnumerable<KeyValuePair<string,IQueryValue>> GetSetColumns() {
				return setColumns.Select( c => new KeyValuePair<string,IQueryValue>(c.Name, new QVar(c.Name).Set(null) ) );
			}
			Query GetPkQuery() {
				var q = new Query(TableName);
				var grpAnd = QGroupNode.And();
				q.Condition = grpAnd;
				foreach (var pkCol in RS.PrimaryKey) {
					grpAnd.Nodes.Add( (QField)pkCol.Name == new QVar(pkCol.Name).Set(null) );
				}
				return q;
			}

			int ExecuteInsertCmd(RecordSet.Row row) {
				if (InsertCmd==null) {
					InsertCmd = DbAdapter.CommandBuilder.GetInsertCommand(TableName, GetSetColumns() );
					InsertCmd.Connection = DbAdapter.Connection;
					InsertCmd.Transaction = DbAdapter.Transaction;
				}
				foreach (DbParameter p in InsertCmd.Parameters) {
					if (p.SourceColumn!=null)
						p.Value = row[p.SourceColumn] ?? DBNull.Value;
				}
				var affected = InsertCmd.ExecuteNonQuery();
				if (autoIncrementCol!=null) {
					row[autoIncrementCol.Name] = DbAdapter.CommandBuilder.DbFactory.GetInsertId(DbAdapter.Connection);
				}
				return affected;
			}

			int ExecuteUpdateCmd(RecordSet.Row row) {
				if (UpdateCmd==null) {
					UpdateCmd = DbAdapter.CommandBuilder.GetUpdateCommand( GetPkQuery(), GetSetColumns() );
					UpdateCmd.Connection = DbAdapter.Connection;
					UpdateCmd.Transaction = DbAdapter.Transaction;
				}
				foreach (DbParameter p in UpdateCmd.Parameters) {
					if (p.SourceColumn!=null)
						p.Value = row[p.SourceColumn] ?? DBNull.Value;
				}
				return UpdateCmd.ExecuteNonQuery();
			}

			int ExecuteDeleteCmd(RecordSet.Row row) {
				if (DeleteCmd==null) {
					DeleteCmd = DbAdapter.CommandBuilder.GetDeleteCommand( GetPkQuery() );
					DeleteCmd.Connection = DbAdapter.Connection;
					DeleteCmd.Transaction = DbAdapter.Transaction;
				}
				foreach (DbParameter p in DeleteCmd.Parameters) {
					if (p.SourceColumn!=null)
						p.Value = row[p.SourceColumn] ?? DBNull.Value;
				}
				return DeleteCmd.ExecuteNonQuery();
			}

			internal int Update() {
				int affected = 0;
				foreach (var row in RS) {
					if ( (row.State&RecordSet.RowState.Added) == RecordSet.RowState.Added) {
						affected += ExecuteInsertCmd(row);
					} else if ( (row.State&RecordSet.RowState.Deleted) == RecordSet.RowState.Deleted ) {
						affected += ExecuteDeleteCmd(row);
					} else if ( (row.State&RecordSet.RowState.Modified) == RecordSet.RowState.Modified ) {
						affected += ExecuteUpdateCmd(row);
					}
					row.AcceptChanges();
				}
				return affected;
			}
		}

		/// <summary>
		/// Represents select query (returned by <see cref="DbDataAdapter.Select"/> method).
		/// </summary>
		public class SelectQuery {
			DbDataAdapter Adapter;
			IDbCommand SelectCommand;
			Query Query;
			IDictionary<string,string> FieldToPropertyMap;

			internal SelectQuery(DbDataAdapter adapter, IDbCommand cmd, Query q, IDictionary<string,string> fldToPropMap) {
				Adapter = adapter;
				SelectCommand = cmd;
				Query = q;
				FieldToPropertyMap = fldToPropMap;
			}

			int DataReaderRecordOffset {
				get {
					return Adapter.ApplyOffset ? Query.RecordOffset : 0;
				}
			}

			/// <summary>
			/// Returns the first record from the query results. 
			/// </summary>
			/// <returns>depending on T, single value or all fields values from the first record</returns>
			public T First<T>() {
				T result = default(T);
				var resTypeCode = Type.GetTypeCode(typeof(T));
				DataHelper.ExecuteReader(SelectCommand, CommandBehavior.SingleRow, DataReaderRecordOffset, 1, 
					(rdr) => {
						result = Read<T>(resTypeCode, rdr);
					} );
				return result;
			}

			/// <summary>
			/// Returns dictionary with first record values.
			/// </summary>
			/// <returns>dictionary with field values or null if query returns zero records.</returns>
			public Dictionary<string,object> ToDictionary() {
				Dictionary<string,object> result = null;
				DataHelper.ExecuteReader(SelectCommand, CommandBehavior.SingleRow, DataReaderRecordOffset, 1, 
					(rdr) => {
						result = ReadDictionary(rdr);
					} );
				return result;
			}

			/// <summary>
			/// Returns a list of dictionaries with all query results .
			/// </summary>
			public List<Dictionary<string,object>> ToDictionaryList() {
				return ToList<Dictionary<string,object>>();
			}

			/// <summary>
			/// Returns a list with all query results.
			/// </summary>
			/// <returns>list with query results</returns>
			public List<T> ToList<T>() {
				var result = new List<T>();
				var resTypeCode = Type.GetTypeCode(typeof(T));
				DataHelper.ExecuteReader(SelectCommand, CommandBehavior.Default, DataReaderRecordOffset, Query.RecordCount,
					(rdr) => {
						result.Add( Read<T>(resTypeCode, rdr) );
					} );
				return result;
			}

			/// <summary>
			/// Returns a list with all query results as <see cref="RecordSet"/>.
			/// </summary>
			public RecordSet ToRecordSet() {
				RecordSet result = null;
				DataHelper.ExecuteReader(SelectCommand, CommandBehavior.Default, DataReaderRecordOffset, Query.RecordCount,
					(rdr) => {
						if (result==null) {
							var rsCols = new List<RecordSet.Column>(rdr.FieldCount);
							var rsPkCols = new List<RecordSet.Column>();

							#if NET_STANDARD
							// lets populate data schema
							if (rdr is DbDataReader) {
								var dbRdr = (DbDataReader)rdr;
								if (dbRdr.CanGetColumnSchema()) {
									foreach (var dbCol in dbRdr.GetColumnSchema()) {
										var c = new RecordSet.Column(dbCol);
										rsCols.Add(c);
										if (dbCol.IsKey.HasValue && dbCol.IsKey.Value)
											rsPkCols.Add(c);
									}
								}
							}
							#endif

							if (rsCols.Count==0) {
								// lets suggest columns by standard IDataReader interface
								for (int i=0; i<rdr.FieldCount; i++) {
									var colName = rdr.GetName(i);
									var colType = rdr.GetFieldType(i);
									rsCols.Add( new RecordSet.Column(colName, colType) );
								}
							}
							result = new RecordSet(rsCols.ToArray(), 1);
							if (rsPkCols.Count>0)
								result.PrimaryKey = rsPkCols.ToArray();
						}

						var rowValues = new object[rdr.FieldCount];
						rdr.GetValues(rowValues);

						result.Add(rowValues).AcceptChanges();
					} );
				return result;
			}

			private T ChangeType<T>(object o, TypeCode typeCode) {
				return (T)Convert.ChangeType( o, typeCode, System.Globalization.CultureInfo.InvariantCulture );
			}

			private Dictionary<string,object> ReadDictionary(IDataReader rdr) {
				var dictionary = new Dictionary<string,object>(rdr.FieldCount);
				for (int i = 0; i < rdr.FieldCount; i++)
					dictionary[rdr.GetName(i)] = rdr.GetValue(i);
				return dictionary;
			}

			private T Read<T>(TypeCode typeCode, IDataReader rdr) {
				// handle primitive single-value result
				if (typeCode!=TypeCode.Object) {
					if (rdr.FieldCount==1) {
						return ChangeType<T>( rdr[0], typeCode);
					} else if (Query.Fields!=null && Query.Fields.Length>0) {
						return ChangeType<T>( rdr[Query.Fields[0].Name], typeCode);
					} else {
						return default(T);
					}
				}
				// T is a structure
				// special handling for dictionaries
				var type = typeof(T);
				if (type==typeof(IDictionary) || type==typeof(IDictionary<string,object>) || type==typeof(Dictionary<string,object>)) {
					return (T)((object)ReadDictionary(rdr));
				}
				// handle as poco model
				var res = Activator.CreateInstance(type);
				DataHelper.MapTo(rdr, res, FieldToPropertyMap);
				return (T)res;
			}
		}

	}
}
