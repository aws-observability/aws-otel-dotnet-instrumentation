namespace TestSimpleApp.EfCore.Data;

public class Post
{
    public int PostId { get; set; }
    public required string Title { get; set; }
    public required string Content { get; set; }

    public int BlogId { get; set; }
    public required Blog Blog { get; set; }
}
