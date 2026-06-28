namespace pgtest.Model
{
    public static class FastTickGenerator
    {
        // ✅ ThreadLocal — each thread gets its own Random instance, no locking needed
        private static readonly ThreadLocal<Random> _rng = new(() => new Random());
        private static readonly string[] _providers = { "Fenics", "Bloomberg", "Reuters" };
        private static readonly char[] _chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789".ToCharArray();

        public static TickData Generate(string[] tickers)
        {
            var rng = _rng.Value!;
            return new TickData
            {
                Symbol = tickers[rng.Next(tickers.Length)],
                Provider = _providers[rng.Next(_providers.Length)],
                Bid = Math.Round(rng.NextDouble() * 2000, 5),
                Ask = Math.Round(rng.NextDouble() * 2000, 5),
                LastPrice = Math.Round(rng.NextDouble() * 2000, 5),
                Volume = rng.NextInt64(0, 100_000_000),
                TickTimestamp = DateTime.UtcNow.AddSeconds(-rng.Next(432000)), // random within last 5 days
                LastUpdatedBy = GenerateUsername(rng),
                LastUpdatedOnUtc = DateTime.UtcNow
            };
        }

        private static string GenerateUsername(Random rng)
        {
            // stackalloc allocates 8 characters on the stack instead of the heap
            // Span<char> is just a view over that stack memory - no heap allocation at all
            // buf - buffer is a temporary holding area for data before it gets used or transformed; we just use it as a var name here
            Span<char> buf = stackalloc char[8];

            // for the 8 characters in the username, pick a random character from _chars and put it in the buffer
            for (int i = 0; i < 8; i++)
                buf[i] = _chars[rng.Next(_chars.Length)];

            // allocate a new string on the heap only once from the characters in the buffer and return it
            return new string(buf);
        }
    }
}
