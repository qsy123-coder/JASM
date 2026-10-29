// Decompiles the function containing each address given as script args, plus its callers.
// Args: [outputDir] [addr1] [addr2] ...
import java.io.File;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Set;

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Program;

public class DumpAt extends GhidraScript {

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        String outDir = args.length > 0 ? args[0] : ".";
        Program prog = currentProgram;
        File out = new File(outDir, prog.getName() + ".targets.txt");

        Set<Function> fns = new LinkedHashSet<>();
        for (int i = 1; i < args.length; i++) {
            long v = Long.parseLong(args[i].replace("0x", ""), 16);
            Address a = prog.getAddressFactory().getDefaultAddressSpace().getAddress(v);
            Function f = prog.getFunctionManager().getFunctionContaining(a);
            println("address " + args[i] + " -> " + (f == null ? "no function" : f.getName()));
            if (f != null) {
                fns.add(f);
                for (Function c : f.getCallingFunctions(monitor)) {
                    fns.add(c);
                }
            }
        }

        DecompInterface di = new DecompInterface();
        di.setSimplificationStyle("decompile");
        di.openProgram(prog);
        try (PrintWriter w = new PrintWriter(out, "UTF-8")) {
            for (Function f : fns) {
                w.println();
                w.println("### " + f.getName() + " @ " + f.getEntryPoint()
                        + " size=" + f.getBody().getNumAddresses());
                w.println("// sig: " + f.getPrototypeString(false, false));
                List<String> callers = new ArrayList<>();
                for (Function c : f.getCallingFunctions(monitor)) {
                    callers.add(c.getName());
                }
                w.println("// callers: " + String.join(", ", callers));
                DecompileResults res = di.decompileFunction(f, 120, monitor);
                if (res != null && res.decompileCompleted()) {
                    String c = res.getDecompiledFunction().getC();
                    if (c.length() > 20000) {
                        c = c.substring(0, 20000) + "\n// ...truncated...";
                    }
                    w.println(c);
                } else {
                    w.println("// decompile failed");
                }
            }
        }
        di.dispose();
        println("wrote " + out.getAbsolutePath() + " (" + fns.size() + " functions)");
    }
}
