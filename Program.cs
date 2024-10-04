using System;
using System.Data.SqlClient;
using System.Text;
using System.IO;
using System.Collections.Generic;
using Newtonsoft.Json;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("Enter the server name:");
        string server = Console.ReadLine();

        Console.WriteLine("Use integrated security? (yes/no):");
        bool integratedSecurity = Console.ReadLine().ToLower() == "yes";

        string userId = null;
        string password = null;

        if (!integratedSecurity)
        {
            Console.WriteLine("Enter the user ID:");
            userId = Console.ReadLine();

            Console.WriteLine("Enter the password:");
            password = Console.ReadLine();
        }

        try
        {
            var connectionStringBuilder = new SqlConnectionStringBuilder
            {
                DataSource = server,
                IntegratedSecurity = integratedSecurity,
                MultipleActiveResultSets = true // Enable MARS
            };

            if (!integratedSecurity)
            {
                connectionStringBuilder.UserID = userId;
                connectionStringBuilder.Password = password;
            }

            using (var connection = new SqlConnection(connectionStringBuilder.ConnectionString))
            {
                connection.Open();
                Console.WriteLine("Connected to the server successfully.");

                // Query to get the list of databases
                var command = new SqlCommand("SELECT name FROM sys.databases", connection);
                using (var reader = command.ExecuteReader())
                {
                    Console.WriteLine("Available databases:");
                    while (reader.Read())
                    {
                        Console.WriteLine(reader.GetString(0));
                    }
                    reader.Close(); // Close the DataReader
                }

                Console.WriteLine("Enter the database name to connect:");
                string database = Console.ReadLine();

                connectionStringBuilder.InitialCatalog = database;
                connection.ChangeDatabase(database);

                try
                {
                    // Retrieve tables and relationships
                    var tables = GetTables(connection);
                    var views = GetViews(connection);
                    var triggers = GetTriggers(connection);
                    var indexes = GetIndexStatistics(connection);
                    var relationships = GetRelationships(connection);

                    // Retrieve columns and data types
                    var tableColumns = GetTableColumns(connection, tables);

                    // Retrieve stored procedures and functions I/O
                    var ioMapping = GetProcedureAndFunctionIO(connection, tables);

                    // Retrieve execution plans
                    var executionPlans = GetExecutionPlans(connection);

                    Console.WriteLine("Getting Obj Dependencies");

                    // Retrieve dependencies for data lineage
                    var dependencies = GetObjectDependencies(connection);

                    // Prepare data for GoJS
                    var goJsData = PrepareGoJsData(tables, views, tableColumns, relationships, ioMapping, executionPlans, dependencies, indexes);

                    // Generate HTML with GoJS
                    var html = GenerateHtmlWithGoJs(goJsData);
                    File.WriteAllText("DatabaseDiagram.html", html);
                    Console.WriteLine("Database diagram saved as DatabaseDiagram.html");
                }
                catch (Exception ex)
                {
                    if (ex.Message.Contains("Data is Null. This method or property cannot be called on Null values."))
                    {
                        Console.WriteLine("Warning: Encountered a null data issue. Continuing execution.");
                    }
                    else
                    {
                        throw;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }

        
    }

    static string[] GetTables(SqlConnection connection)
    {
        var tables = new List<string>();
        var command = new SqlCommand("SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE'", connection);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }
        return tables.ToArray();
    }

    static string[] GetViews(SqlConnection connection)
    {
        var views = new List<string>();
        var command = new SqlCommand("SELECT TABLE_NAME FROM INFORMATION_SCHEMA.VIEWS", connection);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                views.Add(reader.GetString(0));
            }
        }
        return views.ToArray();
    }

    static Dictionary<string, string> GetTriggers(SqlConnection connection)
    {
        var triggers = new Dictionary<string, string>();
        var command = new SqlCommand("SELECT name, OBJECT_DEFINITION(OBJECT_ID) FROM sys.triggers", connection);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                triggers[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }
        return triggers;
    }

    static (string, string)[] GetRelationships(SqlConnection connection)
    {
        var relationships = new List<(string, string)>();
        var command = new SqlCommand(@"
            SELECT 
                FK.TABLE_NAME AS FK_Table,
                PK.TABLE_NAME AS PK_Table
            FROM 
                INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS C
            INNER JOIN 
                INFORMATION_SCHEMA.TABLE_CONSTRAINTS FK ON C.CONSTRAINT_NAME = FK.CONSTRAINT_NAME
            INNER JOIN 
                INFORMATION_SCHEMA.TABLE_CONSTRAINTS PK ON C.UNIQUE_CONSTRAINT_NAME = PK.CONSTRAINT_NAME
            ", connection);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                relationships.Add((reader.GetString(0), reader.GetString(1)));
            }
        }
        return relationships.ToArray();
    }

    static Dictionary<string, List<(string, string)>> GetTableColumns(SqlConnection connection, string[] tables)
    {
        var tableColumns = new Dictionary<string, List<(string, string)>>();
        foreach (var table in tables)
        {
            var columns = new List<(string, string)>();
            var command = new SqlCommand(@"
                SELECT COLUMN_NAME, DATA_TYPE 
                FROM INFORMATION_SCHEMA.COLUMNS 
                WHERE TABLE_NAME = @TableName", connection);
            command.Parameters.AddWithValue("@TableName", table);

            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    columns.Add((reader.GetString(0), reader.GetString(1)));
                }
            }
            tableColumns[table] = columns;
        }
        return tableColumns;
    }

    static Dictionary<string, (List<(string table, int line)> inputs, List<(string table, int line)> outputs, string code)> GetProcedureAndFunctionIO(SqlConnection connection, string[] tables)
    {
        var ioMapping = new Dictionary<string, (List<(string, int)> inputs, List<(string, int)> outputs, string code)>();
        var command = new SqlCommand("SELECT ROUTINE_NAME, ROUTINE_DEFINITION FROM INFORMATION_SCHEMA.ROUTINES WHERE ROUTINE_TYPE IN ('PROCEDURE', 'FUNCTION')", connection);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                string routineName = reader.GetString(0);
                string routineDefinition = reader.IsDBNull(1) ? null : reader.GetString(1);

                var inputs = new List<(string, int)>();
                var outputs = new List<(string, int)>();

                TSql130Parser parser = new TSql130Parser(false);
                IList<ParseError> errors;
                TSqlFragment fragment;
                using (TextReader tr = new StringReader(routineDefinition))
                {
                    fragment = parser.Parse(tr, out errors);
                }

                if (errors.Count == 0)
                {
                    var visitor = new TableReferenceVisitor(tables);
                    fragment.Accept(visitor);

                    foreach (var kvp in visitor.InputTables)
                    {
                        foreach (var line in kvp.Value)
                        {
                            inputs.Add((kvp.Key, line));
                        }
                    }

                    foreach (var kvp in visitor.OutputTables)
                    {
                        foreach (var line in kvp.Value)
                        {
                            outputs.Add((kvp.Key, line));
                        }
                    }
                }
                else
                {
                    Console.WriteLine($"Parsing errors for routine {routineName}: {string.Join(", ", errors.Select(e => e.Message))}");
                }

                ioMapping[routineName] = (inputs, outputs, routineDefinition);
            }
        }
        return ioMapping;
    }

    static Dictionary<string, string> GetExecutionPlans(SqlConnection connection)
    {
        var executionPlans = new Dictionary<string, string>();
        var command = new SqlCommand(@"
            SELECT ROUTINE_NAME, ROUTINE_DEFINITION 
            FROM INFORMATION_SCHEMA.ROUTINES 
            WHERE ROUTINE_TYPE IN ('PROCEDURE', 'FUNCTION')", connection);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                string routineName = reader.GetString(0);
                string routineDefinition = reader.IsDBNull(1) ? null : reader.GetString(1);

                try
                {
                    // Get the execution plan
                    Console.WriteLine($"Retrieving execution plan for {routineName}");
                    string executionPlan = GetExecutionPlanForRoutine(connection, routineDefinition);
                    executionPlans[routineName] = executionPlan;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error retrieving execution plan for {routineName}: {ex.Message}");
                }
            }
        }
        return executionPlans;
    }

    static string GetExecutionPlanForRoutine(SqlConnection connection, string routineDefinition)
{
    // Enable execution plan output
    var enablePlanCommand = new SqlCommand("SET SHOWPLAN_XML ON", connection);
    enablePlanCommand.ExecuteNonQuery();

    string planXml = "";

    try
    {
        var planCommand = new SqlCommand(routineDefinition, connection);
        using (var reader = planCommand.ExecuteReader())
        {
            if (reader.Read())
            {
                planXml = reader.IsDBNull(0) ? null : reader.GetString(0);
            }
        }
    }
    catch (SqlException sqlEx)
    {
        // Check for specific SQL error codes related to invalid column names
        if (sqlEx.Errors.Cast<SqlError>().Any(e => e.Number == 207)) // 207 is the error number for "Invalid column name"
        {
            Console.WriteLine($"Skipping execution plan due to invalid column name: {sqlEx.Message}");
        }
        else
        {
            Console.WriteLine($"SQL Error retrieving execution plan: {sqlEx.Message}");
            foreach (SqlError error in sqlEx.Errors)
            {
                Console.WriteLine($"SQL Error: {error.Message}, Line: {error.LineNumber}, Procedure: {error.Procedure}");
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error retrieving execution plan: {ex.Message}");
    }
    finally
    {
        // Disable execution plan output
        var disablePlanCommand = new SqlCommand("SET SHOWPLAN_XML OFF", connection);
        disablePlanCommand.ExecuteNonQuery();
    }

    return planXml;
}

    static List<IndexInfo> GetIndexStatistics(SqlConnection connection)
    {
        var indexes = new List<IndexInfo>();
        var command = new SqlCommand(@"
            SELECT 
                OBJECT_NAME(s.OBJECT_ID) AS TableName,
                i.name AS IndexName,
                i.type_desc AS IndexType,
                s.user_seeks,
                s.user_scans,
                s.user_lookups,
                s.user_updates,
                s.last_user_seek,
                s.last_user_scan,
                s.last_user_lookup,
                s.last_user_update
            FROM sys.dm_db_index_usage_stats s
            INNER JOIN sys.indexes i ON i.object_id = s.object_id AND i.index_id = s.index_id
            WHERE database_id = DB_ID()", connection);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var indexInfo = new IndexInfo
                {
                    TableName = reader.GetString(0),
                    IndexName = reader.GetString(1),
                    IndexType = reader.GetString(2),
                    UserSeeks = reader.GetInt64(3),
                    UserScans = reader.GetInt64(4),
                    UserLookups = reader.GetInt64(5),
                    UserUpdates = reader.GetInt64(6),
                    LastUserSeek = reader.IsDBNull(7) ? (DateTime?)null : reader.GetDateTime(7),
                    LastUserScan = reader.IsDBNull(8) ? (DateTime?)null : reader.GetDateTime(8),
                    LastUserLookup = reader.IsDBNull(9) ? (DateTime?)null : reader.GetDateTime(9),
                    LastUserUpdate = reader.IsDBNull(10) ? (DateTime?)null : reader.GetDateTime(10),
                };
                indexes.Add(indexInfo);
            }
        }
        return indexes;
    }

    static List<Dependency> GetObjectDependencies(SqlConnection connection)
    {
        var dependencies = new List<Dependency>();
        var command = new SqlCommand(@"
            SELECT 
                OBJECT_NAME(referencing_id) AS ReferencingObject,
                OBJECT_NAME(referenced_id) AS ReferencedObject
            FROM sys.sql_expression_dependencies
            WHERE referenced_id IS NOT NULL", connection);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                try
                {
                    dependencies.Add(new Dependency
                    {
                        ReferencingObject = reader.GetString(0),
                        ReferencedObject = reader.GetString(1)
                    });
                }
                catch (Exception ex)
                {
                    if (ex.Message.Contains("Data is Null. This method or property cannot be called on Null values."))
                    {
                        Console.WriteLine("Warning: Encountered a null data issue. Continuing execution.");
                        continue;
                    }
                    else
                    {
                        throw;
                    }
                }
            }
        }
        return dependencies;
    }

    static string PrepareGoJsData(
        string[] tables,
        string[] views,
        Dictionary<string, List<(string, string)>> tableColumns,
        (string, string)[] relationships,
        Dictionary<string, (List<(string table, int line)> inputs, List<(string table, int line)> outputs, string code)> ioMapping,
        Dictionary<string, string> executionPlans,
        List<Dependency> dependencies,
        List<IndexInfo> indexStatistics)
    {
        var nodes = new List<object>();
        var links = new List<object>();
        var tableUsage = new Dictionary<string, TableUsage>(StringComparer.OrdinalIgnoreCase);

        // Add tables to nodes
        foreach (var table in tables)
        {
            var columns = tableColumns[table];
            var columnDetails = columns.Select(c => new { name = c.Item1, type = c.Item2 }).ToList();
            nodes.Add(new { key = table, name = table, columns = columnDetails, color = "lightblue", category = "Table" });

            var tableKey = table.ToLower();
            if (!tableUsage.ContainsKey(tableKey))
                tableUsage[tableKey] = new TableUsage();
        }

        // Add views to nodes
        foreach (var view in views)
        {
            nodes.Add(new { key = view, name = view, columns = new List<object>(), color = "lightgreen", category = "View" });
        }

        // Add relationships to links
        foreach (var (fkTable, pkTable) in relationships)
        {
            links.Add(new { from = fkTable, to = pkTable });
        }

        // Add dependencies to links
        foreach (var dependency in dependencies)
        {
            links.Add(new { from = dependency.ReferencingObject, to = dependency.ReferencedObject, category = "Dependency" });
        }

        // Process IO mapping and table usage
        foreach (var routine in ioMapping)
        {
            string routineName = routine.Key;
            var inputs = routine.Value.inputs;
            var outputs = routine.Value.outputs;

            foreach (var (tableName, lineNumber) in inputs)
            {
                var tableKey = tableName.ToLower();
                if (!tableUsage.ContainsKey(tableKey))
                    tableUsage[tableKey] = new TableUsage();
                tableUsage[tableKey].inputs.Add(new RoutineReference { RoutineName = routineName, LineNumber = lineNumber });
            }

            foreach (var (tableName, lineNumber) in outputs)
            {
                var tableKey = tableName.ToLower();
                if (!tableUsage.ContainsKey(tableKey))
                    tableUsage[tableKey] = new TableUsage();
                tableUsage[tableKey].outputs.Add(new RoutineReference { RoutineName = routineName, LineNumber = lineNumber });
            }
        }

        // Include execution plans in ioMapping
        var updatedIoMapping = ioMapping.ToDictionary(
            kvp => kvp.Key,
            kvp => new
            {
                inputs = kvp.Value.inputs,
                outputs = kvp.Value.outputs,
                code = kvp.Value.code,
                executionPlan = executionPlans.ContainsKey(kvp.Key) ? executionPlans[kvp.Key] : ""
            }
        );

        // Generate performance insights
        var performanceInsights = GeneratePerformanceInsights(indexStatistics);

        var data = new
        {
            nodeDataArray = nodes,
            linkDataArray = links,
            ioMapping = updatedIoMapping,
            tableUsage = tableUsage,
            dependencies = dependencies,
            performanceInsights = performanceInsights
        };
        return JsonConvert.SerializeObject(data);
    }

    static List<string> GeneratePerformanceInsights(List<IndexInfo> indexStatistics)
    {
        var insights = new List<string>();

        foreach (var index in indexStatistics)
        {
            if (index.UserSeeks == 0 && index.UserScans > 1000)
            {
                insights.Add($"Table '{index.TableName}' with index '{index.IndexName}' is heavily scanned. Consider reviewing indexing strategy.");
            }
            if (index.UserUpdates > 1000 && index.UserSeeks == 0)
            {
                insights.Add($"Index '{index.IndexName}' on table '{index.TableName}' is being updated frequently but not used for seeks. Consider dropping or modifying the index.");
            }
        }

        return insights;
    }

static string GenerateHtmlWithGoJs(string goJsData)
{
    var modelData = JsonConvert.DeserializeObject(goJsData);


    
    return $@"<!DOCTYPE html>
<html>
<head>
    <meta charset=""UTF-8"">
    <title>Database Diagram</title>
    <!-- Include necessary libraries -->
    <script src=""https://unpkg.com/gojs/release/go.js""></script>
    <link rel=""stylesheet"" href=""https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.5/codemirror.min.css"">
    <script src=""https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.5/codemirror.min.js""></script>
    <script src=""https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.5/mode/sql/sql.min.js""></script>
    <link rel=""stylesheet"" href=""https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.5/theme/material.min.css"">
    <link rel=""stylesheet"" href=""https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.5/theme/idea.min.css"">
    <!-- FontAwesome for icons -->
    <link rel=""stylesheet"" href=""https://cdnjs.cloudflare.com/ajax/libs/font-awesome/6.4.0/css/all.min.css"" crossorigin=""anonymous"" referrerpolicy=""no-referrer"" />
    <style>
        /* CSS Styles */
        #indexStatisticsPanel, #performanceInsightsPanel, #executionPlansPanel, #objectDependenciesPanel {{
            padding: 10px;
            border: 1px solid var(--table-border-color);
            background-color: var(--panel-background-color);
            color: var(--text-color);
            margin-bottom: 10px;
        }}
        :root {{
            --background-color: #ffffff;
            --text-color: #000000;
            --panel-background-color: #f0f0f0;
            --highlight-color: #e0e0ff;
            --button-background-color: #e0e0e0;
            --button-text-color: #000000;
            --menu-background-color: #ffffff;
            --menu-text-color: #000000;
            --table-header-background: #d3d3d3;
            --table-row-background: #ffffff;
            --table-row-hover-background: #e9e9e9;
            --table-border-color: #ccc;
        }}
        body.dark-mode {{
            --background-color: #2b2b2b;
            --text-color: #ffffff;
            --panel-background-color: #3c3f41;
            --highlight-color: #4b6eaf;
            --button-background-color: #3c3f41;
            --button-text-color: #ffffff;
            --menu-background-color: #3c3f41;
            --menu-text-color: #ffffff;
            --table-header-background: #4b6eaf;
            --table-row-background: #3c3f41;
            --table-row-hover-background: #4b4f51;
            --table-border-color: #555;
        }}
        body {{
            margin: 0;
            padding: 0;
            background-color: var(--background-color);
            color: var(--text-color);
            font-family: Arial, sans-serif;
            transition: background-color 0.3s ease, color 0.3s ease;
            overflow: hidden;
        }}
        #topPanel {{
            position: absolute;
            top: 0;
            left: 0;
            right: 0;
            height: 50px;
            background-color: var(--panel-background-color);
            transition: background-color 0.3s ease;
            z-index: 1001;
            display: flex;
            align-items: center;
            padding: 0 10px;
        }}
        #menuBar {{
            position: relative;
            display: flex;
        }}
        .menu {{
            position: relative;
            display: inline-block;
        }}
        .menu-button {{
            background-color: transparent;
            color: var(--button-text-color);
            border: none;
            padding: 10px;
            cursor: pointer;
            font-size: 16px;
            transition: color 0.3s ease;
        }}
        .menu-content {{
            display: none;
            position: absolute;
            background-color: var(--menu-background-color);
            min-width: 160px;
            box-shadow: 0px 8px 16px 0px rgba(0,0,0,0.2);
            z-index: 1001;
            opacity: 0;
            transition: opacity 0.3s ease;
        }}
        .menu-content.show {{
            display: block;
            opacity: 1;
        }}
        .menu-content a {{
            color: var(--menu-text-color);
            padding: 12px 16px;
            text-decoration: none;
            display: block;
            cursor: pointer;
        }}
        .menu-content a:hover {{
            background-color: var(--button-background-color);
        }}
        #toggleModeBtn {{
            margin-left: auto;
            background-color: transparent;
            color: var(--button-text-color);
            border: none;
            padding: 10px;
            cursor: pointer;
            font-size: 24px;
            transition: color 0.3s ease;
        }}
        #searchPanel {{
            position: absolute;
            top: 50px;
            left: 0;
            right: 0;
            height: 50px;
            display: flex;
            align-items: center;
            padding-left: 10px;
            padding-right: 10px;
            background-color: var(--panel-background-color);
            transition: background-color 0.3s ease;
            z-index: 1000;
        }}
        #searchPanel input[type=""text""] {{
            flex: 1;
            padding: 8px;
            margin-right: 10px;
            border: 1px solid var(--table-border-color);
            border-radius: 4px;
            background-color: var(--panel-background-color);
            color: var(--text-color);
            transition: background-color 0.3s ease, border-color 0.3s ease;
        }}
        #searchPanel label {{
            margin-right: 10px;
        }}
        #objectTypeFilter {{
            padding: 8px;
            margin-right: 10px;
            border: 1px solid var(--table-border-color);
            border-radius: 4px;
            background-color: var(--panel-background-color);
            color: var(--text-color);
        }}
        #tagFilter {{
            padding: 8px;
            border: 1px solid var(--table-border-color);
            border-radius: 4px;
            background-color: var(--panel-background-color);
            color: var(--text-color);
        }}
        #mainContainer {{
            position: absolute;
            top: 100px;
            left: 0;
            right: 0;
            bottom: 0;
            overflow: hidden;
        }}
        .panel {{
            position: absolute;
            border: 1px solid var(--table-border-color);
            background-color: var(--panel-background-color);
            transition: background-color 0.3s ease;
            box-sizing: border-box;
            overflow: hidden;
            display: flex;
            flex-direction: column;
        }}
        .panel-header {{
            height: 30px;
            background-color: var(--panel-background-color);
            cursor: move;
            display: flex;
            align-items: center;
            padding: 0 5px;
            border-bottom: 1px solid var(--table-border-color);
            z-index: 1;
        }}
        .panel-header .panel-title {{
            flex: 1;
            font-weight: bold;
        }}
        .resizer {{
            position: absolute;
            background: transparent;
        }}
        .resizer.right {{
            width: 5px;
            right: 0;
            top: 0;
            bottom: 0;
            cursor: ew-resize;
        }}
        .resizer.left {{
            width: 5px;
            left: 0;
            top: 0;
            bottom: 0;
            cursor: ew-resize;
        }}
        .resizer.top {{
            height: 5px;
            left: 0;
            right: 0;
            top: 0;
            cursor: ns-resize;
        }}
        .resizer.bottom {{
            height: 5px;
            left: 0;
            right: 0;
            bottom: 0;
            cursor: ns-resize;
        }}
        .resizer.corner {{
            width: 10px;
            height: 10px;
            right: 0;
            bottom: 0;
            cursor: nwse-resize;
        }}
        /* Grid Background */
        #mainContainer {{
            background-size: 25px 25px;
            background-image: linear-gradient(to right, rgba(0,0,0,0.05) 1px, transparent 1px), 
                              linear-gradient(to bottom, rgba(0,0,0,0.05) 1px, transparent 1px);
        }}
        /* Highlighted Line in CodeMirror */
        .highlighted-line {{
            background-color: var(--highlight-color) !important;
            transition: background-color 0.3s ease;
        }}
        /* Tabs */
        .tabs {{
            display: flex;
            background-color: var(--panel-background-color);
            border-bottom: 1px solid var(--table-border-color);
        }}
        .tab-button {{
            flex: 1;
            padding: 10px;
            background-color: transparent;
            border: none;
            cursor: pointer;
            transition: background-color 0.3s ease;
            font-weight: bold;
            color: var(--text-color);
        }}
        .tab-button.active {{
            background-color: var(--button-background-color);
        }}
        .tab-content {{
            display: none;
            flex: 1;
        }}
        .tab-content.active {{
            display: block;
        }}
        /* Table Styles */
        table {{
            width: 100%;
            border-collapse: collapse;
        }}
        th, td {{
            padding: 8px;
            text-align: left;
            border-bottom: 1px solid var(--table-border-color);
            color: var(--text-color);
        }}
        th {{
            background-color: var(--table-header-background);
            font-weight: bold;
        }}
        tr:nth-child(even) {{
            background-color: var(--table-row-background);
        }}
        tr:hover {{
            background-color: var(--table-row-hover-background);
        }}
        /* Bookmark Icons */
        .bookmark-icon {{
            cursor: pointer;
            color: gold;
            font-size: 18px;
        }}
        .bookmark-note {{
            font-style: italic;
            color: var(--text-color);
            margin-left: 10px;
        }}
        /* Responsive Inputs/Outputs Panel */
        #inputsOutputsPanel {{
            display: flex;
            flex-direction: column;
        }}
        #inputsOutputsPanel .tab-content {{
            flex: 1;
            overflow-y: auto;
            padding: 10px;
        }}
        /* Responsive Code Editor */
        #codeEditorContainer {{
            flex: 1;
            height: 100%;
        }}
    </style>
</head>
<body>
    <div id=""topPanel"">
        <div id=""menuBar"">
            <div class=""menu"">
                <button class=""menu-button"">File</button>
                <div class=""menu-content"">
                    <a onclick=""clearLocalStorage()"">Reset UI</a>
                </div>
            </div>
            <div class=""menu"">
                <button class=""menu-button"">Windows</button>
                <div class=""menu-content"">
                    <a onclick=""togglePanel('diagramPanel')"">Diagram</a>
                    <a onclick=""togglePanel('referencesPanel')"">References</a>
                    <a onclick=""togglePanel('editorPanel')"">Code Editor</a>
                    <a onclick=""togglePanel('inputsOutputsPanel')"">Inputs/Outputs</a>
                    <a onclick=""togglePanel('indexStatisticsPanel')"">Index Statistics</a>
                    <a onclick=""togglePanel('performanceInsightsPanel')"">Performance Insights</a>
                    <a onclick=""togglePanel('executionPlansPanel')"">Execution Plans</a>
                    <a onclick=""togglePanel('objectDependenciesPanel')"">Object Dependencies</a>
                </div>
            </div>
        </div>
        <button id=""toggleModeBtn"" title=""Toggle Light/Dark Mode""><i class=""fas fa-moon""></i></button>
    </div>
    <div id=""searchPanel"">
        <input type=""text"" id=""searchBar"" placeholder=""Search for a table or column..."" oninput=""searchDiagram()"" />
        <label><input type=""checkbox"" id=""searchTables"" checked onchange=""searchDiagram()""> Tables</label>
        <label><input type=""checkbox"" id=""searchViews"" checked onchange=""searchDiagram()""> Views</label>
        <label><input type=""checkbox"" id=""searchColumns"" checked onchange=""searchDiagram()""> Columns</label>
        <!-- Advanced search options -->
        <select id=""objectTypeFilter"" onchange=""searchDiagram()"">
            <option value=""all"">All Objects</option>
            <option value=""Table"">Tables</option>
            <option value=""View"">Views</option>
            <option value=""Procedure"">Procedures</option>
            <option value=""Function"">Functions</option>
        </select>
        <input type=""text"" id=""tagFilter"" placeholder=""Tags..."" oninput=""searchDiagram()"" />
    </div>
    <div id=""mainContainer"">
        <!-- Diagram Panel -->
        <div id=""diagramPanel"" class=""panel"" style=""left:0; top:0; width:50%; height:60%;"">
            <div class=""panel-header"">
                <div class=""panel-title"">Diagram</div>
            </div>
            <div id=""myDiagramDiv"" style=""flex:1;""></div>
            <div class=""resizer right""></div>
            <div class=""resizer bottom""></div>
            <div class=""resizer corner""></div>
        </div>
        <!-- References Panel -->
        <div id=""referencesPanel"" class=""panel"" style=""left:50%; top:0; width:50%; height:30%;"">
            <div class=""panel-header"">
                <div class=""panel-title"">References</div>
            </div>
            <div id=""referencesContent"" style=""flex:1; overflow-y:auto; padding:10px;""></div>
            <div class=""resizer left""></div>
            <div class=""resizer bottom""></div>
            <div class=""resizer corner""></div>
        </div>
        <!-- Code Editor Panel -->
        <div id=""editorPanel"" class=""panel"" style=""left:0; top:60%; width:50%; height:40%;"">
            <div class=""panel-header"">
                <div class=""panel-title"">Code Editor</div>
            </div>
            <div style=""flex:1; display:flex; flex-direction: column;"">
                <div class=""tabs"">
                    <button class=""tab-button active"" onclick=""openEditorTab(event, 'codeTab')"">Code</button>
                    <button class=""tab-button"" onclick=""openEditorTab(event, 'executionPlanTab')"">Execution Plan</button>
                </div>
                <div id=""codeTab"" class=""tab-content active"">
                    <div id=""codeEditorContainer""></div>
                </div>
                <div id=""executionPlanTab"" class=""tab-content"">
                    <div id=""executionPlanContainer"" style=""overflow:auto; padding:10px; color: var(--text-color);""></div>
                </div>
            </div>
            <div class=""resizer right""></div>
            <div class=""resizer top""></div>
            <div class=""resizer corner""></div>
        </div>
        <!-- Inputs/Outputs Panel -->
        <div id=""inputsOutputsPanel"" class=""panel"" style=""left:50%; top:30%; width:50%; height:70%;"">
            <div class=""panel-header"">
                <div class=""panel-title"">Inputs/Outputs</div>
            </div>
            <div style=""flex:1; display:flex; flex-direction: column;"">
                <div class=""tabs"">
                    <button class=""tab-button active"" onclick=""openTab(event, 'inputsTab')"">Inputs</button>
                    <button class=""tab-button"" onclick=""openTab(event, 'outputsTab')"">Outputs</button>
                    <button class=""tab-button"" onclick=""openTab(event, 'bookmarksTab')"">Bookmarks</button>
                    <button class=""tab-button"" onclick=""openTab(event, 'performanceTab')"">Performance</button>
                </div>
                <div id=""inputsTab"" class=""tab-content active"">
                    <div id=""inputsContent""></div>
                </div>
                <div id=""outputsTab"" class=""tab-content"">
                    <div id=""outputsContent""></div>
                </div>
                <div id=""bookmarksTab"" class=""tab-content"">
                    <div id=""bookmarksContent""></div>
                </div>
                <div id=""performanceTab"" class=""tab-content"">
                    <div id=""performanceContent""></div>
                </div>
            </div>
            <div class=""resizer left""></div>
            <div class=""resizer top""></div>
            <div class=""resizer corner""></div>
        </div>
        <!-- New Panels for additional data -->
        <div id=""indexStatisticsPanel"" class=""panel"">
            <div class=""panel-header"">
                <div class=""panel-title"">Index Statistics</div>
            </div>
            <div id=""indexStatisticsContent""></div>
        </div>
        <div id=""performanceInsightsPanel"" class=""panel"">
            <div class=""panel-header"">
                <div class=""panel-title"">Performance Insights</div>
            </div>
            <div id=""performanceInsightsContent""></div>
        </div>
        <div id=""executionPlansPanel"" class=""panel"">
            <div class=""panel-header"">
                <div class=""panel-title"">Execution Plans</div>
            </div>
            <div id=""executionPlansContent""></div>
        </div>
        <div id=""objectDependenciesPanel"" class=""panel"">
            <div class=""panel-header"">
                <div class=""panel-title"">Object Dependencies</div>
            </div>
            <div id=""objectDependenciesContent""></div>
        </div>
    </div>
    <script>
        // JavaScript code
        // Declare codeEditor and currentRoutineName in the global scope
        var codeEditor;
        var currentRoutineName = '';
        var bookmarks = [];
        var highestZIndex = 1; // For z-index management


        function init() {{

            document.querySelectorAll('.menu-button').forEach(button => {{
                button.addEventListener('click', function() {{
                    const menuContent = this.nextElementSibling;
                    if (menuContent.classList.contains('show')) {{
                        menuContent.classList.remove('show');
                        setTimeout(() => menuContent.style.display = 'none', 300); // Wait for animation to finish
                    }} else {{
                        menuContent.style.display = 'block';
                        setTimeout(() => menuContent.classList.add('show'), 0); // Trigger animation
                    }}
                }});
            }});

            // Close the dropdown if the user clicks outside of it
            window.addEventListener('click', function(event) {{
                if (!event.target.matches('.menu-button')) {{
                    document.querySelectorAll('.menu-content').forEach(menu => {{
                        if (menu.classList.contains('show')) {{
                            menu.classList.remove('show');
                            setTimeout(() => menu.style.display = 'none', 300);
                        }}
                    }});
                }}
            }});
            var $ = go.GraphObject.make;
            var isDarkMode = document.body.classList.contains('dark-mode');
            var nodeFillColor = isDarkMode ? '#3c3f41' : 'lightblue';
            var textColor = isDarkMode ? '#ffffff' : '#000000';

            var myDiagram =
                $(go.Diagram, 'myDiagramDiv',
                    {{
                        layout: $(go.LayeredDigraphLayout, {{ direction: 90, isInitial: true, isOngoing: false }}),
                        initialContentAlignment: go.Spot.Center,
                        'animationManager.duration': 800
                    }});

            myDiagram.animationManager.isEnabled = true;

            // Define the node template
            myDiagram.nodeTemplate =
                $(go.Node, 'Auto',
                    new go.Binding('category', 'category'),
                    $(go.Shape, 'RoundedRectangle',
                        {{ name: 'SHAPE', strokeWidth: 0, fill: nodeFillColor, portId: '' }},
                        new go.Binding('fill', '', function(data) {{
                            if (data.category === 'View') return 'lightgreen';
                            else return nodeFillColor;
                        }})
                    ),
                    $(go.Panel, 'Table',
                        $(go.RowColumnDefinition, {{ row: 0, separatorStroke: 'black', background: 'lightgray' }}),
                        $(go.TextBlock,
                            {{
                                name: 'TEXT',
                                row: 0,
                                margin: 8,
                                font: 'bold 12pt sans-serif',
                                stroke: textColor,
                                isActionable: true, // Make it clickable
                                click: function(e, obj) {{
                                    var objectName = obj.part.data.name;
                                    document.getElementById('searchBar').value = objectName;
                                    searchDiagram();
                                }}
                            }},
                            new go.Binding('text', 'name')),
                        $(go.Panel, 'Vertical',
                            {{ row: 1, margin: 8 }},
                            new go.Binding('itemArray', 'columns'),
                            {{
                                itemTemplate:
                                    $(go.Panel, 'Horizontal',
                                        $(go.TextBlock,
                                            {{
                                                margin: new go.Margin(0, 5, 0, 0),
                                                stroke: textColor,
                                                isActionable: true, // Make it clickable
                                                click: function(e, obj) {{
                                                    var columnName = obj.data.name;
                                                    document.getElementById('searchBar').value = columnName;
                                                    searchDiagram();
                                                }}
                                            }},
                                            new go.Binding('text', 'name')),
                                        $(go.TextBlock,
                                            {{ stroke: textColor }},
                                            new go.Binding('text', 'type'))
                                    )
                            }}
                        )
                    )
                );

            // Define the link template
            myDiagram.linkTemplate =
                $(go.Link,
                    {{ routing: go.Link.AvoidsNodes, corner: 5 }},
                    new go.Binding('category', 'category'),
                    $(go.Shape),
                    $(go.Shape, {{ toArrow: 'Standard' }})
                );

            // Parse the JSON data
            var modelData = {goJsData};
            window.modelData = modelData; // Make it accessible globally

            // displayIndexStatistics();
            // displayPerformanceInsights();
            // displayExecutionPlans();
            // displayObjectDependencies();

            myDiagram.model = new go.GraphLinksModel(modelData.nodeDataArray, modelData.linkDataArray);

            // Function to search the diagram
            window.searchDiagram = function() {{
                var input = document.getElementById('searchBar').value.toLowerCase();
                var searchTables = document.getElementById('searchTables').checked;
                var searchViews = document.getElementById('searchViews').checked;
                var searchColumns = document.getElementById('searchColumns').checked;
                var objectType = document.getElementById('objectTypeFilter').value;
                var tags = document.getElementById('tagFilter').value.toLowerCase().split(',');

                var referencesPanel = document.getElementById('referencesContent');
                referencesPanel.innerHTML = '';

                if (!input || (!searchTables && !searchViews && !searchColumns)) {{
                    return;
                }}

                var results = [];

                myDiagram.nodes.each(function(node) {{
                    var nodeName = node.data.name.toLowerCase();
                    var nodeCategory = node.data.category;
                    var matchesType = objectType === 'all' || nodeCategory === objectType;
                    var matchesTags = tags.every(tag => !tag.trim() || (node.data.tags && node.data.tags.includes(tag.trim())));

                    if (matchesType && matchesTags) {{
                        if ((searchTables && nodeCategory === 'Table') || (searchViews && nodeCategory === 'View')) {{
                            if (nodeName.includes(input)) {{
                                results.push({{ type: nodeCategory, name: node.data.name, node: node }});
                            }}
                        }}
                        if (searchColumns && node.data.columns) {{
                            node.data.columns.forEach(function(column) {{
                                var columnName = column.name.toLowerCase();
                                if (columnName.includes(input)) {{
                                    results.push({{ type: 'Column', name: node.data.name + '.' + column.name, node: node }});
                                }}
                            }});
                        }}
                    }}
                }});

                results = results.filter((value, index, self) =>
                    index === self.findIndex((t) => (
                        t.type === value.type && t.name === value.name
                    ))
                );

                results.forEach(function(result) {{
                    var referenceItem = document.createElement('div');
                    referenceItem.textContent = result.type + ' - ' + result.name;
                    referenceItem.style.cursor = 'pointer';
                    referenceItem.onclick = function() {{
                        myDiagram.centerRect(result.node.actualBounds);
                        myDiagram.zoomToRect(result.node.actualBounds);
                        highlightNodeAndShowDetails(result);
                    }};
                    referencesPanel.appendChild(referenceItem);
                    // Add fade-in animation
                    referenceItem.style.opacity = 0;
                    setTimeout(function() {{
                        referenceItem.style.transition = 'opacity 0.5s ease';
                        referenceItem.style.opacity = 1;
                    }}, 0);
                }});
            }};

            // Initialize CodeMirror
            codeEditor = CodeMirror(document.getElementById('codeEditorContainer'), {{
                value: '',
                mode: 'text/x-sql',
                lineNumbers: true,
                readOnly: true,
                theme: isDarkMode ? 'material' : 'idea'
            }});

            // Adjust CodeMirror on resize
            window.addEventListener('resize', function() {{
                codeEditor.refresh();
            }});

            // Add click event for CodeMirror gutter (line numbers)
            codeEditor.on('gutterClick', function(cm, line, gutter, event) {{
                var ref = {{
                    RoutineName: currentRoutineName,
                    LineNumber: line + 1
                }};
                toggleBookmark(ref);
            }});

            function highlightNodeAndShowDetails(result) {{
                var node = result.node;
                myDiagram.startTransaction('highlight node');
                myDiagram.clearHighlighteds();
                node.isHighlighted = true;
                myDiagram.commitTransaction('highlight node');

                var inputsContent = document.getElementById('inputsContent');
                var outputsContent = document.getElementById('outputsContent');
                inputsContent.innerHTML = '';
                outputsContent.innerHTML = '';

                var usageKey = result.type === 'Column' ? result.name.split('.')[0] : result.name;
                usageKey = usageKey.toLowerCase();

                var usage = modelData.tableUsage[usageKey] || {{ inputs: [], outputs: [] }};

                displayRoutineReferences(usage.inputs, 'inputsContent', codeEditor);
                displayRoutineReferences(usage.outputs, 'outputsContent', codeEditor);
            }}

            function displayRoutineReferences(references, panelId, codeEditor) {{
                var panel = document.getElementById(panelId);
                panel.innerHTML = '';

                if (references && references.length > 0) {{
                    var table = document.createElement('table');
                    var headerRow = document.createElement('tr');
                    var routineHeader = document.createElement('th');
                    routineHeader.textContent = 'Routine';
                    var lineHeader = document.createElement('th');
                    lineHeader.textContent = 'Line Number';
                    var bookmarkHeader = document.createElement('th');
                    bookmarkHeader.textContent = 'Bookmark';
                    headerRow.appendChild(routineHeader);
                    headerRow.appendChild(lineHeader);
                    headerRow.appendChild(bookmarkHeader);
                    table.appendChild(headerRow);

                    references.forEach(function(ref) {{
                        var row = document.createElement('tr');
                        var routineCell = document.createElement('td');
                        routineCell.textContent = ref.RoutineName;
                        var lineCell = document.createElement('td');
                        lineCell.textContent = ref.LineNumber;
                        lineCell.style.cursor = 'pointer';
                        lineCell.onclick = function(event) {{
                            event.stopPropagation();
                            var routine = modelData.ioMapping[ref.RoutineName];
                            if (routine) {{
                                codeEditor.setValue(routine.code);
                                codeEditor.scrollIntoView({{ line: ref.LineNumber - 1, ch: 0 }}, 100);

                                codeEditor.eachLine(function(lineHandle) {{
                                    codeEditor.removeLineClass(lineHandle, 'background', 'highlighted-line');
                                }});

                                codeEditor.focus();
                                codeEditor.setCursor({{ line: ref.LineNumber - 1, ch: 0 }});
                                codeEditor.addLineClass(ref.LineNumber - 1, 'background', 'highlighted-line');

                                currentRoutineName = ref.RoutineName; // Update current routine

                                // Update execution plan
                                displayExecutionPlan(currentRoutineName);
                            }}
                            toggleBookmark(ref); // Add to bookmarks when clicking line number
                        }};
                        row.appendChild(routineCell);
                        row.appendChild(lineCell);

                        // Add bookmark icon
                        var bookmarkCell = document.createElement('td');
                        var bookmarkIcon = document.createElement('i');
                        bookmarkIcon.className = isBookmarked(ref) ? 'fas fa-bookmark' : 'far fa-bookmark';
                        bookmarkIcon.onclick = function(event) {{
                            event.stopPropagation();
                            toggleBookmark(ref);
                            bookmarkIcon.className = isBookmarked(ref) ? 'fas fa-bookmark' : 'far fa-bookmark';
                        }};
                        bookmarkCell.appendChild(bookmarkIcon);
                        row.appendChild(bookmarkCell);

                        row.onclick = function() {{
                            var routine = modelData.ioMapping[ref.RoutineName];
                            if (routine) {{
                                codeEditor.setValue(routine.code);
                                codeEditor.scrollIntoView({{ line: ref.LineNumber - 1, ch: 0 }}, 100);

                                codeEditor.eachLine(function(lineHandle) {{
                                    codeEditor.removeLineClass(lineHandle, 'background', 'highlighted-line');
                                }});

                                codeEditor.focus();
                                codeEditor.setCursor({{ line: ref.LineNumber - 1, ch: 0 }});
                                codeEditor.addLineClass(ref.LineNumber - 1, 'background', 'highlighted-line');

                                currentRoutineName = ref.RoutineName; // Update current routine

                                // Update execution plan
                                displayExecutionPlan(currentRoutineName);
                            }}
                        }};
                        table.appendChild(row);
                        // Add slide-in animation
                        row.style.transform = 'translateX(-100%)';
                        row.style.opacity = 0;
                        setTimeout(function() {{
                            row.style.transition = 'transform 0.5s ease, opacity 0.5s ease';
                            row.style.transform = 'translateX(0)';
                            row.style.opacity = 1;
                        }}, 0);
                    }});

                    panel.appendChild(table);
                }} else {{
                    panel.textContent = 'No references found.';
                }}
            }}

            window.highlightNodeAndShowDetails = highlightNodeAndShowDetails;

            codeEditor.setValue('');

            document.getElementById('toggleModeBtn').addEventListener('click', function() {{
                document.body.classList.toggle('dark-mode');
                updateDiagramColors();
                updateCodeMirrorTheme();
                updateToggleButtonIcon();
                saveLayout();
            }});

            function updateDiagramColors() {{
                var isDarkMode = document.body.classList.contains('dark-mode');
                var nodeFillColor = isDarkMode ? '#3c3f41' : 'lightblue';
                var textColor = isDarkMode ? '#ffffff' : '#000000';
                myDiagram.startTransaction('change colors');
                myDiagram.nodes.each(function(node) {{
                    var shape = node.findObject('SHAPE');
                    if (shape !== null) shape.fill = nodeFillColor;
                    var text = node.findObject('TEXT');
                    if (text !== null) text.stroke = textColor;
                }});
                myDiagram.links.each(function(link) {{
                    link.path.stroke = textColor;
                }});
                myDiagram.commitTransaction('change colors');
            }}

            function updateCodeMirrorTheme() {{
                var isDarkMode = document.body.classList.contains('dark-mode');
                codeEditor.setOption('theme', isDarkMode ? 'material' : 'idea');
            }}

            function updateToggleButtonIcon() {{
                var isDarkMode = document.body.classList.contains('dark-mode');
                var toggleButton = document.getElementById('toggleModeBtn');
                toggleButton.innerHTML = isDarkMode ? '<i class=""fas fa-sun""></i>' : '<i class=""fas fa-moon""></i>';
            }}

            // Set initial icon based on mode
            updateToggleButtonIcon();

            // Initialize draggable and resizable panels
            initializePanel('diagramPanel');
            initializePanel('referencesPanel');
            initializePanel('editorPanel');
            initializePanel('inputsOutputsPanel');

            // Load saved layout
            loadLayout();

            // Initialize bookmarks
            loadBookmarks();
        }}

        function initializePanel(panelId) {{
            var panel = document.getElementById(panelId);
            var header = panel.querySelector('.panel-header');
            var resizers = panel.querySelectorAll('.resizer');

            // Bring panel to front on mousedown
            panel.addEventListener('mousedown', function() {{
                highestZIndex++;
                panel.style.zIndex = highestZIndex;
                saveLayout();
            }});

            // Make panel draggable
            header.addEventListener('mousedown', function(e) {{
                e.preventDefault();
                var offsetX = e.clientX - panel.offsetLeft;
                var offsetY = e.clientY - panel.offsetTop;

                function mouseMoveHandler(e) {{
                    var newX = e.clientX - offsetX;
                    var newY = e.clientY - offsetY;

                    // Snap to grid positions (25px increments)
                    newX = Math.round(newX / 25) * 25;
                    newY = Math.round(newY / 25) * 25;

                    panel.style.left = newX + 'px';
                    panel.style.top = newY + 'px';
                }}

                function mouseUpHandler() {{
                    document.removeEventListener('mousemove', mouseMoveHandler);
                    document.removeEventListener('mouseup', mouseUpHandler);

                    // Snap to grid
                    var newX = parseInt(panel.style.left);
                    var newY = parseInt(panel.style.top);
                    panel.style.left = Math.round(newX / 25) * 25 + 'px';
                    panel.style.top = Math.round(newY / 25) * 25 + 'px';

                    saveLayout();
                }}

                document.addEventListener('mousemove', mouseMoveHandler);
                document.addEventListener('mouseup', mouseUpHandler);
            }});

            // Make panel resizable
            resizers.forEach(function(resizer) {{
                resizer.addEventListener('mousedown', function(e) {{
                    e.preventDefault();
                    e.stopPropagation();

                    var originalWidth = panel.offsetWidth;
                    var originalHeight = panel.offsetHeight;
                    var originalX = panel.offsetLeft;
                    var originalY = panel.offsetTop;
                    var startX = e.clientX;
                    var startY = e.clientY;

                    function mouseMoveHandler(e) {{
                        if (resizer.classList.contains('right')) {{
                            var width = originalWidth + (e.clientX - startX);
                            panel.style.width = width + 'px';
                        }} else if (resizer.classList.contains('left')) {{
                            var width = originalWidth - (e.clientX - startX);
                            panel.style.width = width + 'px';
                            panel.style.left = originalX + (e.clientX - startX) + 'px';
                        }} else if (resizer.classList.contains('bottom')) {{
                            var height = originalHeight + (e.clientY - startY);
                            panel.style.height = height + 'px';
                        }} else if (resizer.classList.contains('top')) {{
                            var height = originalHeight - (e.clientY - startY);
                            panel.style.height = height + 'px';
                            panel.style.top = originalY + (e.clientY - startY) + 'px';
                        }} else if (resizer.classList.contains('corner')) {{
                            var width = originalWidth + (e.clientX - startX);
                            var height = originalHeight + (e.clientY - startY);
                            panel.style.width = width + 'px';
                            panel.style.height = height + 'px';
                        }}
                        // Adjust CodeMirror editor
                        if (panelId === 'editorPanel') {{
                            codeEditor.refresh();
                        }}
                    }}

                    function mouseUpHandler() {{
                        document.removeEventListener('mousemove', mouseMoveHandler);
                        document.removeEventListener('mouseup', mouseUpHandler);

                        // Snap to grid
                        var newWidth = parseInt(panel.style.width);
                        var newHeight = parseInt(panel.style.height);
                        panel.style.width = Math.round(newWidth / 25) * 25 + 'px';
                        panel.style.height = Math.round(newHeight / 25) * 25 + 'px';

                        saveLayout();
                    }}

                    document.addEventListener('mousemove', mouseMoveHandler);
                    document.addEventListener('mouseup', mouseUpHandler);
                }});
            }});
        }}

        function saveLayout() {{
            var layout = {{
                darkMode: document.body.classList.contains('dark-mode'),
                panels: [],
                bookmarks: bookmarks
            }};
            var panelIds = ['diagramPanel', 'referencesPanel', 'editorPanel', 'inputsOutputsPanel'];
            panelIds.forEach(function(panelId) {{
                var panel = document.getElementById(panelId);
                layout.panels.push({{
                    id: panelId,
                    left: panel.style.left,
                    top: panel.style.top,
                    width: panel.style.width,
                    height: panel.style.height,
                    zIndex: panel.style.zIndex || '1',
                    display: panel.style.display || ''
                }});
            }});
            localStorage.setItem('layout', JSON.stringify(layout));
        }}

        function loadLayout() {{
            var layout = localStorage.getItem('layout');
            if (layout) {{
                layout = JSON.parse(layout);
                if (layout.darkMode) {{
                    document.body.classList.add('dark-mode');
                    updateDiagramColors();
                    updateCodeMirrorTheme();
                    updateToggleButtonIcon();
                }}
                var panelIds = ['diagramPanel', 'referencesPanel', 'editorPanel', 'inputsOutputsPanel'];
                layout.panels.forEach(function(panelData) {{
                    var panel = document.getElementById(panelData.id);
                    panel.style.left = panelData.left;
                    panel.style.top = panelData.top;
                    panel.style.width = panelData.width;
                    panel.style.height = panelData.height;
                    panel.style.zIndex = panelData.zIndex;
                    panel.style.display = panelData.display;
                    if (parseInt(panelData.zIndex) > highestZIndex) {{
                        highestZIndex = parseInt(panelData.zIndex);
                    }}
                }});
                bookmarks = layout.bookmarks || [];
                loadBookmarks();
            }}
        }}

        function clearLocalStorage() {{
            localStorage.removeItem('layout');
            location.reload();
        }}

        // Tab functionality
        function openTab(event, tabId) {{
            var tabContents = event.currentTarget.parentNode.parentNode.querySelectorAll('.tab-content');
            var tabButtons = event.currentTarget.parentNode.querySelectorAll('.tab-button');

            tabContents.forEach(function(content) {{
                content.classList.remove('active');
            }});
            tabButtons.forEach(function(button) {{
                button.classList.remove('active');
            }});

            document.getElementById(tabId).classList.add('active');
            event.currentTarget.classList.add('active');

            if (tabId === 'performanceTab') {{
                displayPerformanceInsights();
            }}
        }}

        // Open editor tabs
        function openEditorTab(event, tabId) {{
            var tabContents = event.currentTarget.parentNode.parentNode.querySelectorAll('.tab-content');
            var tabButtons = event.currentTarget.parentNode.querySelectorAll('.tab-button');

            tabContents.forEach(function(content) {{
                content.classList.remove('active');
            }});
            tabButtons.forEach(function(button) {{
                button.classList.remove('active');
            }});

            document.getElementById(tabId).classList.add('active');
            event.currentTarget.classList.add('active');

            if (tabId === 'executionPlanTab') {{
                displayExecutionPlan(currentRoutineName);
            }}
        }}

        function displayExecutionPlan(routineName) {{
            var executionPlanContainer = document.getElementById('executionPlanContainer');
            var routine = modelData.ioMapping[routineName];
            if (routine && routine.executionPlan) {{
                // Display the execution plan XML
                executionPlanContainer.textContent = routine.executionPlan;
            }} else {{
                executionPlanContainer.textContent = 'No execution plan available.';
            }}
        }}

        // Toggle panels from Windows menu
        function togglePanel(panelId) {{
            var panel = document.getElementById(panelId);
            if (panel.style.display === 'none') {{
                panel.style.display = '';
            }} else {{
                panel.style.display = 'none';
            }}
            saveLayout();
        }}

        // Bookmarks functionality
        function loadBookmarks() {{
            var bookmarksContent = document.getElementById('bookmarksContent');
            bookmarksContent.innerHTML = '';

            if (bookmarks.length > 0) {{
                var table = document.createElement('table');
                var headerRow = document.createElement('tr');
                var routineHeader = document.createElement('th');
                routineHeader.textContent = 'Routine';
                var lineHeader = document.createElement('th');
                lineHeader.textContent = 'Line Number';
                var noteHeader = document.createElement('th');
                noteHeader.textContent = 'Note';
                headerRow.appendChild(routineHeader);
                headerRow.appendChild(lineHeader);
                headerRow.appendChild(noteHeader);
                table.appendChild(headerRow);

                bookmarks.forEach(function(ref) {{
                    var row = document.createElement('tr');
                    var routineCell = document.createElement('td');
                    routineCell.textContent = ref.RoutineName;
                    var lineCell = document.createElement('td');
                    lineCell.textContent = ref.LineNumber;
                    var noteCell = document.createElement('td');
                    noteCell.textContent = ref.note || '';
                    noteCell.style.cursor = 'pointer';
                    noteCell.onclick = function() {{
                        var note = prompt('Enter a note for this bookmark:', ref.note || '');
                        if (note !== null) {{
                            ref.note = note;
                            noteCell.textContent = note;
                            saveLayout();
                        }}
                    }};

                    row.onclick = function() {{
                        var routine = modelData.ioMapping[ref.RoutineName];
                        if (routine) {{
                            codeEditor.setValue(routine.code);
                            codeEditor.scrollIntoView({{ line: ref.LineNumber - 1, ch: 0 }}, 100);

                            codeEditor.eachLine(function(lineHandle) {{
                                codeEditor.removeLineClass(lineHandle, 'background', 'highlighted-line');
                            }});

                            codeEditor.focus();
                            codeEditor.setCursor({{ line: ref.LineNumber - 1, ch: 0 }});
                            codeEditor.addLineClass(ref.LineNumber - 1, 'background', 'highlighted-line');

                            currentRoutineName = ref.RoutineName; // Update current routine

                            // Update execution plan
                            displayExecutionPlan(currentRoutineName);
                        }}
                    }};
                    row.appendChild(routineCell);
                    row.appendChild(lineCell);
                    row.appendChild(noteCell);
                    table.appendChild(row);
                }});
                bookmarksContent.appendChild(table);
            }} else {{
                bookmarksContent.textContent = 'No bookmarks added.';
            }}
        }}

        function isBookmarked(ref) {{
            return bookmarks.some(function(b) {{
                return b.RoutineName === ref.RoutineName && b.LineNumber === ref.LineNumber;
            }});
        }}

        function toggleBookmark(ref) {{
            var index = bookmarks.findIndex(function(b) {{
                return b.RoutineName === ref.RoutineName && b.LineNumber === ref.LineNumber;
            }});

            if (index === -1) {{
                // Add to bookmarks
                bookmarks.push(ref);
            }} else {{
                // Remove from bookmarks
                bookmarks.splice(index, 1);
            }}

            saveLayout();
            loadBookmarks();
        }}

        // Display performance insights
        function displayPerformanceInsights() {{
            var performanceContent = document.getElementById('performanceContent');
            performanceContent.innerHTML = '';

            var insights = modelData.performanceInsights;
            if (insights && insights.length > 0) {{
                insights.forEach(function(insight) {{
                    var insightItem = document.createElement('div');
                    insightItem.textContent = insight;
                    performanceContent.appendChild(insightItem);
                }});
            }} else {{
                performanceContent.textContent = 'No performance issues detected.';
            }}
        }}

        
        

        window.addEventListener('DOMContentLoaded', init);
    </script>
</body>
</html>";
}


    class TableUsage
    {
        public List<RoutineReference> inputs { get; set; } = new List<RoutineReference>();
        public List<RoutineReference> outputs { get; set; } = new List<RoutineReference>();
    }

    class RoutineReference
    {
        public string RoutineName { get; set; }
        public int LineNumber { get; set; }
        public string note { get; set; } // Added note property
    }

    class TableReferenceVisitor : TSqlFragmentVisitor
    {
        private readonly HashSet<string> _tables;
        public Dictionary<string, List<int>> InputTables { get; } = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<int>> OutputTables { get; } = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

        private Stack<string> _contextStack = new Stack<string>();

        public TableReferenceVisitor(IEnumerable<string> tables)
        {
            _tables = new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase);
            _contextStack.Push("Input");
        }

        public override void Visit(NamedTableReference node)
        {
            string tableName = node.SchemaObject.BaseIdentifier.Value;
            if (_tables.Contains(tableName))
            {
                int lineNumber = node.StartLine;
                string context = _contextStack.Peek();
                if (context == "Input")
                {
                    if (!InputTables.ContainsKey(tableName))
                        InputTables[tableName] = new List<int>();
                    InputTables[tableName].Add(lineNumber);
                }
                else if (context == "Output")
                {
                    if (!OutputTables.ContainsKey(tableName))
                        OutputTables[tableName] = new List<int>();
                    OutputTables[tableName].Add(lineNumber);
                }
            }
        }

        public override void Visit(InsertStatement node)
        {
            _contextStack.Push("Output");
            node.InsertSpecification.Target.Accept(this);
            _contextStack.Pop();

            _contextStack.Push("Input");
            node.InsertSpecification.InsertSource?.Accept(this);
            _contextStack.Pop();
        }

        public override void Visit(UpdateStatement node)
        {
            _contextStack.Push("Output");
            node.UpdateSpecification.Target.Accept(this);
            _contextStack.Pop();

            _contextStack.Push("Input");
            node.UpdateSpecification.WhereClause?.Accept(this);
            node.UpdateSpecification.FromClause?.Accept(this);
            foreach (var setClause in node.UpdateSpecification.SetClauses)
            {
                setClause.Accept(this);
            }
            _contextStack.Pop();
        }

        public override void Visit(DeleteStatement node)
        {
            _contextStack.Push("Output");
            node.DeleteSpecification.Target.Accept(this);
            _contextStack.Pop();

            _contextStack.Push("Input");
            node.DeleteSpecification.FromClause?.Accept(this);
            node.DeleteSpecification.WhereClause?.Accept(this);
            _contextStack.Pop();
        }

        public override void Visit(SelectStatement node)
        {
            _contextStack.Push("Input");
            base.Visit(node);
            _contextStack.Pop();
        }
    }

    class IndexInfo
    {
        public string TableName { get; set; }
        public string IndexName { get; set; }
        public string IndexType { get; set; }
        public long UserSeeks { get; set; }
        public long UserScans { get; set; }
        public long UserLookups { get; set; }
        public long UserUpdates { get; set; }
        public DateTime? LastUserSeek { get; set; }
        public DateTime? LastUserScan { get; set; }
        public DateTime? LastUserLookup { get; set; }
        public DateTime? LastUserUpdate { get; set; }
    }

    class Dependency
    {
        public string ReferencingObject { get; set; }
        public string ReferencedObject { get; set; }
    }
}
