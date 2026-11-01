using System.Diagnostics;
using System.Text.Json;
using MySqlConnector;

namespace CJora.Routing.Persistence;

public static class SessionSql
{
    public static MySqlCommand Command(MySqlConnection connection, MySqlTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command=new MySqlCommand(sql,connection,transaction) { CommandTimeout=5 };
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name,parameter.Value ?? DBNull.Value);
        return command;
    }
    public static DateTime Utc(MySqlDataReader reader,int ordinal) => DateTime.SpecifyKind(reader.GetDateTime(ordinal),DateTimeKind.Utc);
    public static async Task Audit(MySqlConnection connection,MySqlTransaction transaction,Guid operation,Guid? program,Guid? user,Guid? session,
        string operationName,string entityType,Guid? entity,string action,string state,string correlation,string result = "SUCCEEDED", Guid? workloadActor = null)
    {
        await using var command=Command(connection,transaction,"""
            INSERT INTO API_LOG (UUID,PROGRAM_UUID,USER_UUID,ACTOR_TYPE,ACTOR_UUID,SESSION_UUID,CORRELATION_ID,TRACE_ID,OPERATION_UUID,
              OPERATION_NAME,ENTITY_TYPE,ENTITY_UUID,ACTION_CODE,RESULT_CODE,CHANGE_DETAILS,CREATED_DATETIME)
            VALUES (@uuid,@program,@user,@actorType,@actor,@session,@correlation,@trace,@operation,@name,@type,@entity,@action,@result,@changes,UTC_TIMESTAMP(6))
            """,("@uuid",Guid.NewGuid().ToString("D")),("@program",program?.ToString("D")),("@user",user?.ToString("D")),
            ("@actorType",workloadActor.HasValue?"WORKLOAD":user.HasValue?"USER":"WORKLOAD"),("@actor",workloadActor?.ToString("D")??user?.ToString("D")??"00000000-0000-0000-0000-000000000001"),
            ("@session",session?.ToString("D")),("@correlation",correlation),("@trace",Activity.Current?.TraceId.ToString()),("@operation",operation.ToString("D")),
            ("@name",operationName),("@type",entityType),("@entity",entity?.ToString("D")),("@action",action),("@result",result),("@changes",JsonSerializer.Serialize(new {state})));
        await command.ExecuteNonQueryAsync();
    }
}