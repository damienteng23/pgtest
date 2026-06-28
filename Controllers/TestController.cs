using Microsoft.AspNetCore.Mvc;
using pgtest.Interface;

namespace pgtest.Controllers
{
    [ApiController]
    [Route("test")]
    public class TestController : ControllerBase
    {
        private readonly IDataStore _store;

        public TestController(
            IDataStore store)
        {
            _store = store;
        }

        [HttpPost("createcsv")]
        public async Task<IActionResult> Createcsv(int countOfRows, int countOfFiles)
        {
            await _store.CreateCsvAsync(countOfRows, countOfFiles);
            return Ok();
        }

        [HttpPost("insertCsvToPg")]
        public async Task<IActionResult> Insert(string pathOfzstdFile)
        {
            await _store.InsertIntoPg3(pathOfzstdFile);
            return Ok();
        }
    }
}
