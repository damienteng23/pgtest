namespace pgtest.Interface
{
    public interface IDataStore
    {
        Task CreateCsvAsync(int count, int fileCount = 1);
        Task InsertIntoPg3(string pathOfzstdFile);

    }
}