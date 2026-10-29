// Targeted dossier: decompiles only functions that reference interesting strings
// or call interesting imports. Args: [outputDir]
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
import ghidra.program.model.listing.FunctionIterator;
import ghidra.program.model.listing.Program;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.mem.MemoryBlock;
import ghidra.program.model.symbol.RefType;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;
import ghidra.program.model.symbol.ReferenceManager;

public class SelectDump extends GhidraScript {

    private static final String[] NEEDLES = {
        "Rocker_lod_texture", "Lod0_texture", "鸣潮贴图",
        "主DLL", "CBTProc", "Hook", "开始注入",
        "共享内存", "d3dx.ini", "d3d11.dll", "Rocket.Features",
        "ShootingMutex", "注入延迟"
    };

    private static final String[] IMPORTS = {
        "CreateRemoteThread", "VirtualAllocEx", "WriteProcessMemory", "LoadLibraryW",
        "SetWindowsHookExW", "CallNextHookEx", "CreateFileMappingW", "CreateProcessW",
        "OpenProcess", "CreateProcessWithTokenW", "MapViewOfFile", "NtCreateThreadEx",
        "SetWindowsHookExA", "UnhookWindowsHookEx", "GameOverlayRenderer"
    };

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        String outDir = args.length > 0 ? args[0] : ".";
        Program prog = currentProgram;
        File out = new File(outDir, prog.getName() + ".select.txt");

        Set<Function> hits = new LinkedHashSet<>();
        ReferenceManager rm = prog.getReferenceManager();

        // 1) strings found in memory -> referencing functions
        Memory mem = prog.getMemory();
        for (MemoryBlock b : mem.getBlocks()) {
            if (!b.isInitialized() || b.getSize() > 64L * 1024 * 1024) {
                continue;
            }
            if (!b.isRead()) {
                continue;
            }
            byte[] data = new byte[(int) Math.min(b.getSize(), 64L * 1024 * 1024)];
            int n = mem.getBytes(b.getStart(), data);
            for (String needle : NEEDLES) {
                byte[] u8 = needle.getBytes("UTF-8");
                byte[] u16 = needle.getBytes("UTF-16LE");
                collect(prog, data, n, b.getStart(), u8, rm, hits);
                collect(prog, data, n, b.getStart(), u16, rm, hits);
            }
        }

        // 2) imports -> calling functions
        for (Function f : allFunctions(prog)) {
            String name = f.getName();
            for (String imp : IMPORTS) {
                if (name.contains(imp)) {
                    addCallers(prog, f, hits);
                }
            }
        }

        DecompInterface di = new DecompInterface();
        di.setSimplificationStyle("decompile");
        di.openProgram(prog);
        try (PrintWriter w = new PrintWriter(out, "UTF-8")) {
            w.println("# target=" + prog.getName() + " functions selected=" + hits.size());
            for (Function f : hits) {
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
        println("wrote " + out.getAbsolutePath() + " (" + hits.size() + " functions)");
    }

    private List<Function> allFunctions(Program prog) {
        List<Function> list = new ArrayList<>();
        FunctionIterator it = prog.getFunctionManager().getFunctions(true);
        while (it.hasNext()) {
            list.add(it.next());
        }
        return list;
    }

    private void addCallers(Program prog, Function target, Set<Function> hits) {
        for (Function c : target.getCallingFunctions(monitor)) {
            hits.add(c);
        }
        hits.add(target);
    }

    private void collect(Program prog, byte[] data, int n, Address base, byte[] pat,
            ReferenceManager rm, Set<Function> hits) throws Exception {
        for (int i = 0; i + pat.length <= n; i++) {
            boolean ok = true;
            for (int k = 0; k < pat.length; k++) {
                if (data[i + k] != pat[k]) {
                    ok = false;
                    break;
                }
            }
            if (!ok) {
                continue;
            }
            Address a = base.add(i);
            ReferenceIterator ri = rm.getReferencesTo(a);
            while (ri.hasNext()) {
                Reference r = ri.next();
                if (r.getReferenceType() == RefType.DATA || r.getReferenceType() == RefType.READ) {
                    Function f = prog.getFunctionManager().getFunctionContaining(r.getFromAddress());
                    if (f != null) {
                        hits.add(f);
                        addCallers(prog, f, hits);
                    }
                }
            }
        }
    }
}
