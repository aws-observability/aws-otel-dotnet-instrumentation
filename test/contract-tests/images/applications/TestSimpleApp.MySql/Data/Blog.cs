namespace TestSimpleApp.MySql.Data;

public class Blog
{
    public int BlogId { get; set; }
    public required string Url { get; set; }

    public IList<Post> Posts { get; } = [];
}
