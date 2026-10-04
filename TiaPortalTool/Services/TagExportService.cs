namespace TiaPortalTool.Services;

public sealed class TagExportService
{
    public List<TagTableData> CollectTagTables(dynamic plcSoftware)
    {
        var result = new List<TagTableData>();
        CollectGroup(plcSoftware.TagTableGroup, string.Empty, result);
        return result;
    }

    public IReadOnlyList<string> ApplyEdits(dynamic plcSoftware, TagTableData edited)
    {
        dynamic? table = FindTagTableByPath(plcSoftware.TagTableGroup, edited.GroupPath, edited.TableName);
        if (table is null)
        {
            return new[] { $"Skipped {edited.TableName}: could not locate this tag table in the live project." };
        }

        return new[] { (string)ApplyTagTableEdits(table, edited) };
    }

    private static void CollectGroup(dynamic group, string groupPath, List<TagTableData> result)
    {
        foreach (dynamic tagTable in group.TagTables)
        {
            result.Add(CollectTagTable(tagTable, groupPath));
        }

        foreach (dynamic subGroup in group.Groups)
        {
            string subPath = string.IsNullOrEmpty(groupPath) ? (string)subGroup.Name : $"{groupPath}/{subGroup.Name}";
            CollectGroup(subGroup, subPath, result);
        }
    }

    private static TagTableData CollectTagTable(dynamic tagTable, string groupPath)
    {
        var data = new TagTableData { TableName = tagTable.Name, GroupPath = groupPath };
        var languages = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (dynamic tag in tagTable.Tags)
        {
            var comments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (dynamic item in tag.Comment.Items)
            {
                string lang = item.Language.ToString();
                comments[lang] = (string?)item.Text ?? string.Empty;
                languages.Add(lang);
            }

            data.Rows.Add(new TagRowData
            {
                Name = tag.Name,
                DataType = tag.DataTypeName,
                Address = (string?)tag.LogicalAddress ?? string.Empty,
                Comments = comments
            });
        }

        data.Languages = languages.ToList();
        return data;
    }

    private static string ApplyTagTableEdits(dynamic table, TagTableData edited)
    {
        var tagsByName = new Dictionary<string, dynamic>(StringComparer.OrdinalIgnoreCase);
        foreach (dynamic tag in table.Tags)
        {
            tagsByName[(string)tag.Name] = tag;
        }

        int updatedTags = 0;
        int updatedComments = 0;

        foreach (var row in edited.Rows)
        {
            if (!tagsByName.TryGetValue(row.Name, out var tag))
            {
                continue;
            }

            bool tagTouched = false;
            foreach (var pair in row.Comments)
            {
                string language = pair.Key;
                string newText = pair.Value;
                foreach (dynamic item in tag.Comment.Items)
                {
                    if (string.Equals(item.Language.ToString(), language, StringComparison.OrdinalIgnoreCase))
                    {
                        string currentText = (string?)item.Text ?? string.Empty;
                        if (currentText != newText)
                        {
                            item.Text = newText;
                            updatedComments++;
                            tagTouched = true;
                        }
                        break;
                    }
                }
            }

            if (tagTouched)
            {
                updatedTags++;
            }
        }

        return $"{edited.TableName}: updated {updatedComments} comment value(s) across {updatedTags} tag(s).";
    }

    private static dynamic? FindTagTableByPath(dynamic rootGroup, string groupPath, string tableName)
    {
        dynamic group = rootGroup;
        if (!string.IsNullOrEmpty(groupPath))
        {
            foreach (var segment in groupPath.Split('/'))
            {
                dynamic? next = null;
                foreach (dynamic candidate in group.Groups)
                {
                    if (string.Equals((string)candidate.Name, segment, StringComparison.OrdinalIgnoreCase))
                    {
                        next = candidate;
                        break;
                    }
                }

                if (next is null)
                {
                    return null;
                }

                group = next;
            }
        }

        foreach (dynamic t in group.TagTables)
        {
            if (string.Equals((string)t.Name, tableName, StringComparison.OrdinalIgnoreCase))
            {
                return t;
            }
        }

        return null;
    }
}
