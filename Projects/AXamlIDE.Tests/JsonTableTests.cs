using System;
using AXamlIDE.Services.Debugging;
using Xunit;

namespace AXamlIDE.Tests;

public class JsonTableTests
{
    [Fact]
    public void An_array_of_objects_becomes_typed_columns()
    {
        const string json = """
            [
              {"id": 1, "name": "Ada", "score": 1.5, "active": true, "joined": "2020-01-02T03:04:05Z", "key": "6f9619ff-8b86-d011-b42d-00cf4fc964ff"},
              {"id": 2, "name": null, "score": 2, "active": false, "joined": "2021-05-06", "key": "6f9619ff-8b86-d011-b42d-00cf4fc964fe", "extra": {"a": 1}}
            ]
            """;
        var table = JsonTables.TryRead(json)!;

        Assert.Equal(["#", "id", "name", "score", "active", "joined", "key", "extra"], table.Columns);
        Assert.Equal("$", table.Path);
        var first = table.Rows[0];
        Assert.Equal(0L, first[0]);
        Assert.Equal(1L, first[1]);
        Assert.Equal("Ada", first[2]);
        Assert.Equal(1.5m, first[3]);
        Assert.Equal(true, first[4]);
        Assert.IsType<DateTime>(first[5]);
        Assert.IsType<Guid>(first[6]);
        Assert.Null(first[7]);                      // missing in this row
        Assert.Null(table.Rows[1][2]);              // null
        Assert.Equal(2m, table.Rows[1][3]);         // the column is decimal because of 1.5
        Assert.Equal("{\"a\": 1}", table.Rows[1][7]);
    }

    [Fact]
    public void The_largest_array_inside_an_object_is_used()
    {
        var table = JsonTables.TryRead("""{"total": 2, "tags": ["x"], "data": [{"a": 1}, {"a": 2}]}""")!;
        Assert.Equal("$.data", table.Path);
        Assert.Equal(2, table.Rows.Count);
    }

    [Fact]
    public void Values_dates_as_objects_and_not_json()
    {
        var values = JsonTables.TryRead("[3, 1, 2]")!;
        Assert.Equal(["#", "Value"], values.Columns);
        Assert.Equal(2L, values.Rows[2][1]);

        var dates = JsonTables.TryRead("""[{"when": {"isValidDateTime": true, "year": 2026, "month": 10, "day": 19, "hour": 14, "minute": 30, "second": 0}}]""")!;
        Assert.Equal(new DateTime(2026, 10, 19, 14, 30, 0), dates.Rows[0][1]);

        Assert.Null(JsonTables.TryRead("hello"));
        Assert.Null(JsonTables.TryRead("{\"a\": 1}"));
        Assert.Null(JsonTables.TryRead("[1, 2"));
    }

    [Fact]
    public void Mixed_columns_fall_back_to_text_so_they_still_sort()
    {
        var table = JsonTables.TryRead("""[{"v": 1}, {"v": "two"}, {"v": "2020-01-01 is not iso-only"}]""")!;
        Assert.Equal("1", table.Rows[0][1]);
        Assert.Equal("two", table.Rows[1][1]);
    }
}
