using System;
using System.Collections.Generic;

var srcLabels = new List<string> { "A" };
var tgtLabels = new List<string>();
var newLabels = srcLabels.Except(tgtLabels).ToList();

if (newLabels.Count > 0) {
    foreach (var label in newLabels) {
        var idx = srcLabels.IndexOf(label);
        Console.WriteLine($"idx = {idx}");
        if (idx > 0) {
            Console.WriteLine($"AFTER case: srcLabels[{idx-1}] = {srcLabels[idx - 1]}");
        } else {
            Console.WriteLine($"BEFORE case: trying to access srcLabels[1]");
            if (srcLabels.Count > 1) {
                Console.WriteLine($"srcLabels[1] = {srcLabels[1]}");
            } else {
                Console.WriteLine($"ERROR: Only have {srcLabels.Count} labels, can't access srcLabels[1]");
            }
        }
    }
}
