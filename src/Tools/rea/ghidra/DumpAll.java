// Exports a readable dossier: program info, memory blocks, imports/exports,
// strings, functions with decompiled C. Args: [outputDir]
import java.io.File;
import java.io.PrintWriter;
import java.util.ArrayList;
import java.util.List;

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Data;
import ghidra.program.model.listing.DataIterator;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.FunctionIterator;
import ghidra.program.model.listing.Program;
import ghidra.program.model.mem.MemoryBlock;
import ghidra.program.model.symbol.Symbol;
import ghidra.program.model.symbol.SymbolIterator;
import ghidra.program.model.symbol.SymbolType;

public class DumpAll extends GhidraScript {

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        String outDir = args.length > 0 ? args[0] : ".";
        Program prog = currentProgram;
        File out = new File(outDir, prog.getName() + ".dump.txt");

        DecompInterface di = new DecompInterface();
        di.toggleCCode(true);
        di.toggleSyntaxTree(true);
        di.setSimplificationStyle("decompile");
        di.openProgram(prog);

        try (PrintWriter w = new PrintWriter(out, "UTF-8")) {
            w.println("## PROGRAM");
            w.println("name=" + prog.getName());
            w.println("executableFormat=" + prog.getExecutableFormat());
            w.println("language=" + prog.getLanguage().getLanguageID());
            w.println("compilerSpec=" + prog.getCompilerSpec().getCompilerSpecID());
            w.println("imageBase=" + prog.getImageBase());
            w.println("sha256=" + prog.getExecutableSHA256());
            w.println();

            w.println("## MEMORY BLOCKS");
            for (MemoryBlock b : prog.getMemory().getBlocks()) {
                w.printf("%s %s size=0x%x r=%s w=%s x=%s init=%s%n",
                        b.getName(), b.getStart(), b.getSize(),
                        b.isRead(), b.isWrite(), b.isExecute(), b.isInitialized());
            }
            w.println();

            w.println("## SYMBOLS");
            SymbolIterator si = prog.getSymbolTable().getAllSymbols(false);
            while (si.hasNext()) {
                Symbol s = si.next();
                if (s.getSymbolType() == SymbolType.FUNCTION) {
                    continue; // listed with functions
                }
                w.printf("%s  %s  [%s]%n", s.getAddress(), s.getName(), s.getSymbolType());
            }
            w.println();

            w.println("## STRINGS");
            DataIterator dit = prog.getListing().getDefinedData(true);
            while (dit.hasNext()) {
                Data d = dit.next();
                if (d == null || d.getValue() == null) {
                    continue;
                }
                String t = d.getValue().toString();
                if (d.getDataType().getName().toLowerCase().contains("string")
                        || d.getDataType().getName().toLowerCase().contains("unicode")) {
                    t = t.replace("\r", "\\r").replace("\n", "\\n");
                    w.printf("%s  %s%n", d.getAddress(), t);
                }
            }
            w.println();

            w.println("## FUNCTIONS");
            List<Function> funcs = new ArrayList<>();
            FunctionIterator fit = prog.getFunctionManager().getFunctions(true);
            while (fit.hasNext()) {
                funcs.add(fit.next());
            }
            for (Function f : funcs) {
                w.println();
                w.println("### " + f.getName() + " @ " + f.getEntryPoint()
                        + " size=" + f.getBody().getNumAddresses()
                        + " ext=" + f.isExternal());
                w.println("// signature: " + f.getPrototypeString(false, false));
                List<String> callers = new ArrayList<>();
                for (Function caller : f.getCallingFunctions(monitor)) {
                    callers.add(caller.getName());
                }
                w.println("// callers: " + String.join(", ", callers));
                w.println("// refcount=" + prog.getReferenceManager().getReferenceCountTo(f.getEntryPoint()));
                if (f.isExternal() || f.isThunk()) {
                    continue;
                }
                DecompileResults res = di.decompileFunction(f, 60, monitor);
                if (res != null && res.decompileCompleted()) {
                    String c = res.getDecompiledFunction().getC();
                    if (c.length() > 12000) {
                        c = c.substring(0, 12000) + "\n// ...truncated...";
                    }
                    w.println(c);
                } else {
                    w.println("// decompile failed: " + (res == null ? "null" : res.getErrorMessage()));
                }
            }
            di.dispose();
        }
        println("wrote " + out.getAbsolutePath());
    }
}
