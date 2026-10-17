// Labels every IL2CPP method from methods.tsv, then decompiles the methods in targets.tsv.
// Args: <methods.tsv> <targets.tsv> <out_dir>
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.*;
import java.nio.file.*;
import java.util.*;

public class DecompTargets extends GhidraScript {
    Map<Long, String> names = new HashMap<>();

    String clean(String s) { return s.replaceAll("[^A-Za-z0-9_.$]", "_"); }

    Function ensureFunction(Address a) throws Exception {
        Function f = getFunctionAt(a);
        if (f != null) return f;
        disassemble(a);
        String n = names.get(a.subtract(currentProgram.getImageBase()));
        return createFunction(a, n);
    }

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        Address base = currentProgram.getImageBase();
        SymbolTable st = currentProgram.getSymbolTable();
        int labelled = 0;
        for (String line : Files.readAllLines(Paths.get(args[0]))) {
            String[] p = line.split("\t");
            long rva = Long.parseLong(p[0], 16);
            String n = clean(p[2] + "__" + p[3]);
            if (names.containsKey(rva)) continue;
            names.put(rva, n);
            try { st.createLabel(base.add(rva), n, SourceType.USER_DEFINED); labelled++; } catch (Exception e) { }
        }
        println("labelled " + labelled);

        // Throw helpers never return; without this, decompiled bodies run into the next method.
        for (String line : Files.readAllLines(Paths.get(args[3]))) {
            if (line.isBlank()) continue;
            Address a = base.add(Long.parseLong(line.trim(), 16));
            Function f = ensureFunction(a);
            if (f == null) { disassemble(a); f = createFunction(a, "noreturn_" + line.trim()); }
            if (f != null) f.setNoReturn(true);
        }

        List<String[]> targets = new ArrayList<>();
        for (String line : Files.readAllLines(Paths.get(args[1]))) targets.add(line.split("\t"));

        // Create target functions and their direct callees so calls print by name.
        for (String[] t : targets) {
            Function f = ensureFunction(base.add(Long.parseLong(t[0], 16)));
            if (f == null) continue;
            InstructionIterator it = currentProgram.getListing().getInstructions(f.getBody(), true);
            while (it.hasNext()) {
                Instruction ins = it.next();
                if (!ins.getFlowType().isCall()) continue;
                for (Address c : ins.getFlows()) {
                    try { ensureFunction(c); } catch (Exception e) { }
                }
            }
        }

        DecompInterface ifc = new DecompInterface();
        ifc.openProgram(currentProgram);
        File out = new File(args[2]);
        out.mkdirs();
        int ok = 0;
        for (String[] t : targets) {
            Function f = getFunctionAt(base.add(Long.parseLong(t[0], 16)));
            if (f == null) { println("no function " + t[1] + "." + t[2]); continue; }
            DecompileResults r = ifc.decompileFunction(f, 180, monitor);
            String c = r.decompileCompleted() ? r.getDecompiledFunction().getC() : "// FAILED: " + r.getErrorMessage() + "\n";
            try (FileWriter w = new FileWriter(new File(out, clean(t[1]) + ".c"), true)) {
                w.write("// " + t[1] + "." + t[2] + " @ 0x" + t[0] + "\n" + c + "\n");
            }
            if (r.decompileCompleted()) ok++;
        }
        println("decompiled " + ok + " of " + targets.size());
    }
}
