using System.Diagnostics;
using RT.Util;
using RT.Util.Collections;

namespace CallWaitingExpectedTime;

// log: queue length (max length), agent utilisation %

internal class Program
{
    static void Main(string[] args)
    {
        var rnd = new Random(12345);
        var waits = new AutoDictionary<int, List<double>>(_ => new());
        int total = 0;
        while (true)
        {
#if false // random sampling
            var calls = genCalls(rnd, 5, 5.0, 5.0);
            waits[-1].AddRange(calls.Select(c => c.Waiting));
            waits[-1].Sort();
            Console.WriteLine(Ut.FormatCsvRow(-1, waits[-1].Average(), waits[-1][waits[-1].Count / 2], waits[-1].Count));
            for (int patience = 0; patience <= 80; patience++)
            {
                for (int s = 0; s < 1000; s++)
                {
                    var call = calls[rnd.Next(calls.Count)];
                    if (call.Waiting < patience)
                        continue;
                    waits[patience].Add(call.Waiting - patience);
                }
                if (waits[patience].Count == 0)
                    continue;
                waits[patience].Sort();
                Console.WriteLine(Ut.FormatCsvRow(patience, waits[patience].Average(), waits[patience][waits[patience].Count / 2], waits[patience].Count));
            }
#else // sample everything
            for (int i = 0; i < 500 / 20; i++)
            {
                total += 20;
                var calls = Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => genCalls(Random.Shared, 5, 5.0, 5.0)))).GetAwaiter().GetResult().SelectMany(cc => cc);
                //waits[-1].AddRange(calls.Select(c => c.Waiting));
                //waits[-1].Sort();
                //Console.WriteLine(Ut.FormatCsvRow(-1, waits[-1].Average(), waits[-1][waits[-1].Count / 2], waits[-1].Count));
                //for (int s = 0; s < calls.Count / 10; s++)
                foreach (var call in calls)
                    for (int patience = 0; patience <= 80; patience++)
                        if (patience < call.Waiting)
                            waits[patience].Add(call.Waiting - patience);
                Console.Title = $"Total: {total:#,0}";
            }
            for (int patience = 0; patience <= 80; patience++)
            {
                if (waits[patience].Count == 0)
                    continue;
                waits[patience].Sort();
                Console.WriteLine(Ut.FormatCsvRow(patience, waits[patience].Average(), waits[patience][waits[patience].Count / 2], waits[patience].Count)); // waits[-1].Count(w => w < patience) / (double)waits[-1].Count
            }
#endif
        }
    }

    static void GeneralStats()
    {
        var counts = new List<int>();
        var instants = new List<double>();
        var p50s = new List<double>();
        var p95s = new List<double>();
        var p100s = new List<double>();
        for (int seed = 12345; seed <= 12355; seed++)
        {
            var rnd = new Random(seed);
            var calls = genCalls(rnd, 50, 5.0, 5.0);
            counts.Add(calls.Count);
            p50s.Add(calls[calls.Count / 2].Waiting);
            p95s.Add(calls[calls.Count * 95 / 100].Waiting);
            p100s.Add(calls[^1].Waiting);
            instants.Add(calls.Count(c => c.Waiting == 0) / (double)calls.Count);
        }
        counts.Sort();
        p50s.Sort();
        p95s.Sort();
        p100s.Sort();
        instants.Sort();
    }

    static List<Call> genCalls(Random rnd, int agentCount, double medianWaitLimit, double meanCallDuration)
    {
        var calls = new List<Call>();
#if false // Slow and naive
        while (true)
        {
            for (int i = 0; i < 500; i++)
                calls.Add(new Call { ArrivedTime = rnd.NextDouble() * 100_000, TalkDuration = exp(rnd, 5.0) });
            Compute(calls, 10);
            var ordered = calls.OrderBy(c => c.Waiting).ToList();
            var p50 = ordered[ordered.Count / 2].Waiting;
            bool end = p50 > 5.0;
            if (end || calls.Count % 500 == 0)
                Console.WriteLine($"Calls: {calls.Count}, waiting median={p50:0.0}m, 95%={ordered[ordered.Count * 95 / 100].Waiting:0.0}m, max={ordered[^1].Waiting:0.0}");
            if (end)
                break;
        }
#else // binary search
        int step = 1;
        bool growing = true;
        while (true)
        {
            Console.WriteLine($"Calls: {calls.Count:#,0}, step: {step:#,0}");
            if (step > 0)
                for (int i = 0; i < step; i++)
                    calls.Add(new Call { ArrivedTime = rnd.NextDouble() * 100_000, TalkDuration = exp(rnd, meanCallDuration) });
            else
                calls.RemoveRange(calls.Count + step, -step); // this removal overshoots all the time, and because adding back is random it might never get back up to target again, but it gets very close. Ideally this should use a Span instead of permanently removing items
            Compute(calls, agentCount);
            var ordered = calls.OrderBy(c => c.Waiting).ToList();
            var p50 = ordered[ordered.Count / 2].Waiting;
            if (p50 < medianWaitLimit)
            {
                step = growing ? (Math.Abs(step) * 2) : (Math.Abs(step) / 2);
            }
            else
            {
                step = -Math.Abs(step) / 2;
                growing = false;
            }
            if (step == 0)
            {
                Console.WriteLine($"Calls: {calls.Count:#,0}, waiting median={p50:0.00}m, 95%={ordered[ordered.Count * 95 / 100].Waiting:0.00}m, max={ordered[^1].Waiting:0.00}");
                return ordered;
            }
        }
#endif
    }

    static double exp(Random rnd, double mean)
    {
        return -Math.Log(1 - rnd.NextDouble()) * mean;
    }

    static void Compute(List<Call> calls, int agentCount)
    {
        var events = new PriorityQueue<Event, double>();
        events.EnqueueRange(calls.Select(c => (new Event { Kind = EventKind.CallArrived, Time = c.ArrivedTime, Call = c }, c.ArrivedTime)));

        var callsWaiting = new Queue<Call>();
        var agents = new Call[agentCount];

        while (events.Count > 0)
        {
            var evt = events.Dequeue();
            var now = evt.Time;

            if (evt.Kind == EventKind.CallArrived)
            {
                callsWaiting.Enqueue(evt.Call);
            }
            else if (evt.Kind == EventKind.CallEnded)
            {
                Debug.Assert(agents[evt.Agent] == evt.Call);
                agents[evt.Agent] = null;
            }
            // give queued calls to free agents
            for (int a = 0; a < agents.Length; a++)
                if (agents[a] == null && callsWaiting.Count > 0)
                {
                    agents[a] = callsWaiting.Dequeue();
                    agents[a].AnsweredTime = now;
                    var evtEnd = new Event { Kind = EventKind.CallEnded, Time = agents[a].EndedTime, Agent = a, Call = agents[a] };
                    events.Enqueue(evtEnd, evtEnd.Time);
                }
        }
        Debug.Assert(callsWaiting.Count == 0);
    }
}

class Event
{
    public double Time;
    public Call Call;
    public int Agent;
    public EventKind Kind;
}

enum EventKind { CallArrived, CallEnded }

class Call
{
    public double ArrivedTime; // fixed; minutes
    public double TalkDuration; // fixed; minutes, after answered

    public double AnsweredTime; // computed
    public double EndedTime => AnsweredTime + TalkDuration;
    public double Waiting => AnsweredTime - ArrivedTime;
}
