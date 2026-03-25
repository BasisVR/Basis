using System;
using System.Collections.Generic;
using System.Globalization;

namespace BasisNetworkServer.BasisNetworking
{
    /// <summary>
    /// Advanced word filter using homoglyph detection and trigram-based false positive prevention.
    /// Adapted from KoboldKare's word-filter package by naelstrof.
    /// Detects banned words even when obfuscated with Unicode lookalikes or inserted characters,
    /// while avoiding false positives like matching "ass" in "assignment".
    /// </summary>
    public static class BasisWordFilter
    {
        private struct TextElementInfo
        {
            public string Value;
            public int CharIndex;
        }

        private static readonly HashSet<string> Trigrams = new HashSet<string> {
"ati", "tio", "nes", "ter", "ica", "non", "ent", "all", "per", "eri", "abl", "tin", "ver", "ali", "pre", "ant", "tra", "ate", "con", "sti", 
"nte", "tri", "rat", "lin", "pro", "oni", "ion", "nti", "ing", "ist", "lat", "ene", "lit", "tic", "ste", "ari", "iti", "ove", "res", "cal", 
"the", "nde", "rin", "men", "era", "str", "ine", "ran", "dis", "int", "gra", "ili", "ona", "mat", "olo", "ato", "oph", "ere", "tro", "tiv", 
"ero", "ast", "ect", "log", "pho", "ani", "chi", "der", "ina", "tor", "und", "min", "sta", "ori", "nat", "cti", "emi", "her", "ula", "cat", 
"est", "rop", "ell", "met", "nis", "lis", "ous", "par", "for", "che", "rac", "phi", "mis", "rap", "ill", "rea", "les", "ele", "ost", "rec", 
"sto", "cha", "eli", "and", "com", "ini", "ers", "ric", "lli", "ome", "ris", "len", "oli", "ria", "unc", "car", "sub", "tat", "anc", "nta", 
"mon", "rou", "one", "ess", "gen", "cul", "ace", "erm", "lan", "enc", "man", "ive", "pha", "ten", "eti", "tom", "eni", "oma", "ici", "rit", 
"pla", "ete", "aph", "ros", "ngl", "out", "ida", "nin", "art", "ont", "tur", "ara", "act", "ssi", "rom", "sin", "uns", "cho", "end", "ndi", 
"ble", "etr", "osi", "lle", "ogi", "shi", "ang", "tho", "ono", "nit", "bil", "nco", "hor", "ize", "ath", "oto", "ita", "din", "cro", "ide", 
"ntr", "ret", "ons", "ort", "tan", "ora", "sup", "cor", "tal", "ach", "ise", "rie", "hal", "rot", "den", "erc", "ogr", "unt", "uri", "ron", 
"cen", "ore", "har", "lla", "liz", "idi", "pol", "hin", "qui", "omi", "ven", "sco", "ass", "tis", "roc", "dia", "eno", "ren", "col", "iat",
"ala", "sem", "ino", "ind", "las", "iou", "pos", "esi", "lic", "hro", "tab", "arc", "lar", "bra", "por", "tte", "rti", "inc", "abi", "ami",
"sse", "kin", "mer", "thi", "upe", "edi", "ale", "onc", "ard", "ish", "pin", "mic", "tar", "spe", "ton", "eme", "ond", "red", "hea", "nch",
"oun", "ens", "han", "ifi", "rep", "ber", "nal", "ana", "ert", "she", "ema", "eta", "llo", "mor", "nge", "pat", "vel", "uni", "phy", "nth",
"tel", "eco", "ura", "dro", "err", "sio", "pen", "ial", "hyp", "can", "ple", "tre", "nce", "fer", "oti", "isi", "ear", "cra", "ite", "lac",
"cto", "mar", "imp", "adi", "omo", "oth", "ner", "lik", "ain", "isc", "cop", "rio", "oge", "ser", "lec", "thr", "tie", "qua", "nic", "rch",
"emo", "hol", "cer", "ral", "ram", "wor", "sit", "rma", "ame", "eat", "cou", "nsi", "ree", "sne", "ian", "phe", "dic", "ico", "oro", "ack",
"ure", "pti", "niz", "unp", "ans", "app", "iza", "ole", "aci", "hem", "rad", "zin", "spi", "ust", "rem", "ern", "rid", "cin", "tit", "rab",
"igh", "dec", "ult", "oly", "pri", "sen", "odi", "nar", "alo", "hil", "gin", "pan", "opi", "ler", "epi", "nia", "una", "oll", "cre", "omp",
"och", "ano", "att", "hon", "ett", "eph", "ela", "asi", "cri", "til", "ola", "lou", "spo", "iss", "opo", "ins", "tia", "are", "ena", "des",
"oca", "mit", "uti", "top", "rol", "rre", "bar", "ith", "rog", "rel", "pal", "ibl", "lor", "lia", "ade", "ies", "ane", "arr", "nst", "usl",
"lea", "iso", "sha", "riz", "nto", "scr", "eth", "hen", "age", "roo", "cke", "our", "eou", "nom", "isa", "los", "ras", "cia", "chr", "ong",
"fic", "sca", "net", "atr", "cla", "bro", "rte", "rmi", "ger", "nci", "war", "amp", "mal", "erp", "rai", "erl", "ime", "ata", "dne", "nou",
"rdi", "que", "esc", "mel", "zat", "tru", "ote", "nre", "nne", "ick", "orm", "tha", "sur", "his", "rri", "ept", "nse", "icu", "cep", "ead",
"ose", "ich", "loc", "rag", "cur", "ann", "eve", "lie", "nos", "bri", "acc", "ref", "aut", "sol", "gat", "une", "ode", "win", "unr", "rth",
"erv", "ama", "gis", "izi", "mul", "osp", "let", "hel", "som", "lam", "tol", "odo", "ien", "ope", "ivi", "ull", "tle", "ave", "sat", "tch",
"cit", "ede", "ito", "eas", "usn", "lab", "cle", "ice", "ile", "imi", "hyd", "elo", "lig", "sal", "sce", "oce", "ght", "ppe", "bal", "pli",
"tim", "dra", "orp", "nda", "ntl", "nsu", "dit", "dem", "oco", "cel", "eci", "ima", "bac", "agi", "mpe", "sul", "ndo", "ngi", "aro", "uto",
"osc", "mou", "opa", "dio", "erb", "orr", "erg", "spa", "sch", "ock", "cea", "emp", "ila", "hot", "neu", "toc", "siv", "amb", "rse", "pic",
"aco", "icr", "itt", "oss", "gic", "rei", "san", "gal", "lio", "rif", "ful", "inf", "apo", "obi", "ein", "mac", "cap", "uli", "nan", "tif",
"nab", "reg", "edl", "ake", "ydr", "rim", "rig", "uro", "del", "mil", "eca", "use", "ndr", "fla", "yst", "tti", "tac", "usi", "mme", "pit",
"ype", "ext", "ifo", "vin", "son", "nac", "fie", "ker", "erf", "pec", "het", "bla", "squ", "ega", "lop", "ung", "sel", "eto", "onf", "eur",
"edn", "sho", "mas", "bli", "bel", "iol", "sci", "pse", "hou", "omm", "rev", "ril", "rip", "atu", "hom", "ord", "pul", "unf", "ota", "lne",
"lot", "acr", "arb", "itu", "lti", "tos", "gon", "sor", "nop", "gre", "bul", "ilo", "oid", "tem", "eud", "mpl", "ech", "ban", "bat", "vis",
"don", "nni", "ire", "pon", "yin", "exp", "not", "gan", "amm", "rni", "dri", "hoo", "isp", "cis", "tai", "duc", "ngu", "cos", "coc", "cte",
"pte", "sil", "rmo", "bit", "ign", "glo", "hic", "fis", "orn", "lim", "erd", "ese", "umb", "cli", "low", "tiz", "equ", "hyl", "amo", "avi",
"fra", "emb", "lem", "dle", "clo", "seu", "lum", "otr", "eso", "sma", "rod", "pra", "ail", "nor", "owe", "rus", "ute", "cas", "lon", "sph",
"rme", "cta", "dat", "mes", "unb", "sid", "ash", "val", "flo", "fin", "gne", "cid", "mot", "opl", "ctr", "ict", "arm", "iri", "bre", "mid",
"ife", "syn", "pis", "udo", "rna", "gar", "eal", "mph", "rob", "unm", "rco", "npr", "tou", "bin", "pet", "scu", "bor", "arg", "leg", "bur",
"noc", "ssa", "ors", "mpa", "hos", "epr", "itr", "stu", "oin", "uin", "lut", "aca", "lif", "rcu", "ulo", "iva", "ase", "rib", "ypo", "ben",
"sec", "ago", "evi", "ibi", "tog", "rde", "reb", "ape", "rne", "ott", "vit", "rra", "epa", "rli", "nif", "tes", "plo", "esp", "ysi", "alt",
"gle", "ism", "eac", "agg", "utt", "bas", "ffe", "sar", "llu", "sso", "cio", "ecu", "abo", "opt", "pas", "rsi", "ual", "asc", "aff", "rph",
"reo", "thy", "olu", "air", "uct", "oot", "bol", "mag", "vol", "umi", "mol", "ceo", "lag", "pto", "pot", "rge", "ipp", "flu", "pel", "enn",
"ier", "roi", "nve", "onv", "rav", "bio", "nol", "lid", "def", "ted", "onp", "ctu", "api", "tea", "iga", "gro", "ffi", "hag", "dep", "nel",
"sic", "udi", "chl", "nso", "nca", "rro", "cie", "nam", "row", "hop", "mpo", "woo", "sop", "iac", "lep", "ars", "hie", "tag", "eab", "wee",
"oso", "pod", "alc", "tid", "typ", "fle", "tyl", "pir", "med", "dan", "nem", "hys", "nap", "inn", "ndu", "pie", "hit", "nsa", "ham", "ige",
"vat", "wis", "tas", "rsh", "ece", "oxi", "die", "rta", "gge", "pil", "roa", "hes", "nsp", "sab", "dea", "tam", "sib", "arl", "ido", "aur",
"mos", "nex", "mak", "ume", "pac", "hab", "rbo", "rgi", "uat", "lus", "cum", "aki", "cot", "pur", "mbe", "lip", "ood", "fac", "whi", "uta",
"ych", "ink", "oga", "nio", "ylo", "bon", "bea", "ped", "smo", "imm", "ank", "tot", "gas", "gui", "rer", "irr", "osa", "nen", "nas", "soc",
"van", "tet", "ira", "epe", "nec", "rov", "sig", "mbr", "aga", "phr", "yli", "gri", "tua", "uff", "ebr", "gna", "efi", "sia", "cam", "old",
"urr", "ocy", "ski", "ocr", "gam", "riv", "lde", "nea", "uma", "dor", "urn", "ugh", "lph", "ado", "tig", "mmi", "alm", "hri", "pyr", "dom",
"nog", "een", "cru", "omb", "arp", "tip", "ipe", "rbi", "tec", "oci", "nim", "nid", "cet", "iph", "uit", "oba", "yll", "leu", "coa", "bou",
"rce", "hia", "hth", "hip", "mma", "efu", "gul", "hum", "rga", "ecr", "ruc", "geo", "opp", "loo", "uis", "lom", "gli", "cut", "ulp", "iot",
"rle", "aba", "iab", "add", "ada", "uth", "mbo", "rba", "hyt", "tap", "sla", "nfe", "ipa", "eol", "onr", "tib", "iar", "nsh", "mod", "rum",
"hee", "erw", "eis", "arn", "eva", "pop", "epo", "tne", "urg", "inu", "onn", "tli", "aln", "lad", "pea", "gla", "uck", "ckl", "yro", "gel",
"sth", "fil", "agr", "hob", "hlo", "sac", "lys", "ean", "bes", "lai", "ump", "ios", "atc", "smi", "spr", "cki", "loi", "hio", "ppi", "exc",
"cil", "irc", "esh", "cir", "tee", "apa", "oct", "dul", "abb", "mpr", "ois", "acu", "ngr", "igi", "sim", "ovi", "psi", "nli", "nfo", "eng",
"uar", "alu", "nct", "ces", "nie", "iro", "imo", "rst", "blo", "bus", "spl", "doc", "cab", "unh", "occ", "neo", "fre", "adr", "orc", "ava",
"riu", "mbl", "axi", "egr", "has", "nfi", "sea", "nna", "uch", "egi", "noi", "agn", "rrh", "yto", "org", "ule", "cip", "oar", "rpo", "iop",
"dol", "nad", "eop", "sli", "dar", "mus", "unn", "boo", "toi", "leo", "but", "nke", "unl", "chu", "sep", "ipl", "vic", "inv", "exa", "nga",
"fri", "plu", "eed", "mmo", "hir", "oug", "div", "mbi", "ark", "nsc", "hat", "coe", "lte", "ool", "nme", "boa", "dge", "apt", "git", "ubs",
"eda", "yri", "ibr", "exi", "yth", "tta", "rvi", "mun", "dre", "ary", "cyt", "fro", "ush", "obl", "dip", "mba", "ocu", "edu", "lib", "ncr",
"ibe", "lob", "uss", "var", "rar", "asp", "cus", "rso", "ncu", "dif", "aus", "ymp", "iff", "cry", "rve", "edo", "ddl", "imb", "rci", "det",
"sis", "fol", "ncl", "syc", "hra", "yph", "dac", "hae", "run", "reh", "own", "psy", "ntu", "xtr", "gua", "mie", "lau", "mia", "ebo", "nag",
"tul", "ubi", "rof", "uen", "uci", "ngo", "urs", "enu", "rhe", "heo", "ndl", "pou", "ias", "chy", "abe", "vil", "efe", "rsa", "iqu", "nqu", 
"gni", "dow", "poi", "ook", "ehe", "oke", "hec", "ipo", "dab", "pid", "evo", "hed", "kle", "raf", "oon", "ske", "ube", "nvi", "unw", "yla",
"ttl", "aly", "hur", "ggi", "lap", "bst", "umm", "eep", "hre", "ves", "gno", "eck", "stl", "nfl", "fec", "ewa", "hap", "luc", "ebe", "rca",
"oty", "eut", "dde", "opr", "ork", "ppr", "dal", "gor", "gio", "wel", "dli", "mpi", "rew", "oxy", "lav", "uno", "nno", "yan", "uil", "oda",
"upp", "fli", "off", "fus", "bir", "ais", "igr", "yna", "lve", "coi", "rpe", "esu", "edr", "bbe", "dev", "orb", "aul", "via", "mea", "dou",
"ngn", "thu", "anu", "teo", "pun", "ege", "sym", "nob", "wit", "tub", "oat", "lod", "eer", "wat", "ges", "mop", "dig", "gie", "ycl", "lyc",
"nma", "alg", "iet", "aun", "aer", "cac", "rha", "ecl", "fur", "sau", "urb", "eig", "ery", "utr", "set", "hne", "led", "ppo", "hte", "npe",
"bed", "pht", "ket", "nle", "ova", "cyc", "eba", "iom", "mog", "ipi", "sum", "wea", "hog", "cau", "eru", "tun", "tto", "rva", "nod", "bis",
"rda", "ssu", "clu", "nig", "cog", "eel", "yti", "omy", "nul", "cif", "aso", "fir", "nfr", "ror", "igo", "far", "aft", "uts", "ccu", "lyp",
"rho", "erh", "ubl", "nfa", "dos", "rpr", "hle", "rbe", "swa", "put", "lei", "sme", "lun", "mai", "avo", "dam", "hod", "cco", "ogn", "ubb",
"shl", "ipt", "sou", "ogl", "onl", "zoo", "efo", "ppl", "twi", "cad", "sie", "dil", "usc", "leb", "itc", "alk", "hid", "ude", "ony", "lee",
"hei", "eam", "diu", "lay", "onm", "mut", "nep", "gea", "gma", "foo", "bet", "tir", "ddi", "mpt", "gue", "ken", "bbl", "eak", "ltr", "asa",
"ops", "wer", "sle", "dop", "yle", "hym", "hai", "lur", "rtu", "nha", "idd", "whe", "bot", "cci", "lyt", "yco", "hoc", "npa", "eet", "mmu",
"moc", "ald", "ewo", "mni", "rke", "jec", "liv", "igg", "oad", "uou", "ioc", "tox", "wal", "rhi", "asm", "ego", "uad", "aud", "bbi", "uba",
"obe", "unk", "tme", "exe", "chn", "ugg", "pta", "ogo", "mir", "ggl", "raw", "iog", "agu", "rup", "miz", "nfu", "ait", "alp", "gil", "aem",
"hau", "moo", "oil", "fal", "hiz", "rys", "see", "swe", "tfu", "shn", "fte", "ewi", "bru", "cys", "nhe", "alv", "oac", "diz", "uln", "oen",
"ses", "ded", "hac", "sty", "ryp", "vul", "ryo", "ssn", "mbu", "efl", "kie", "epl", "azo", "rdo", "aps", "unv", "urt", "poc", "oul", "dog",
"vid", "ffl", "nki", "lev", "loa", "tud", "adv", "ogg", "vir", "ior", "ibu", "abs", "lae", "aug", "eff", "pap", "rul", "aeo", "ocl", "odu",
"umo", "lym", "wom", "abr", "ewe", "cod", "opu", "cks", "oop", "azi", "too", "coo", "yme", "upr", "tod", "ubc", "igu", "ssl", "esm", "lex",
"obs", "dyn", "jac", "eum", "ody", "bic", "nil", "bia", "euc", "esa", "nbe", "exo", "fan", "enz", "cce", "gia", "oel", "gly", "ebu", "ypt",
"ivo", "obo", "fun", "neg", "sif", "pne", "did", "elt", "abu", "epu", "uco", "eho", "iod", "suc", "oos", "sed", "lue", "fel", "swi", "dim",
"vor", "cem", "shr", "hyr", "tut", "ozo", "glu", "uda", "hex", "slo", "num", "efa", "oqu", "lel", "aqu", "yce", "rut", "pag", "oom", "rla",
"sap", "voc", "adm", "niu", "tma", "pia", "iel", "eld", "hun", "vas", "got", "yma", "fyi", "owl", "was", "rto", "onu", "osm", "blu", "upl",
"vag", "deb", "gou", "rau", "gur", "aye", "pai", "meg", "ulf", "oan", "eav", "sam", "pip", "yra", "sna", "fen", "wan", "ubt", "ouc", "kil",
"get", "ulu", "tum", "rud", "obb", "dur", "roe", "egu", "eor", "mur", "rub", "udd", "kis", "scl", "pom", "icl", "ibb", "nee", "myo", "fes",
"uce", "bec", "ohe", "awa", "fas", "pig", "usa", "nur", "elu", "sus", "rfe", "hep", "alb", "gem", "rru", "sua", "ike", "hme", "ciz", "eos",
"apl", "bab", "edg", "kel", "sag", "owi", "eoc", "rsu", "wil", "vio", "ldi", "nai", "ymo", "uan", "tax", "rki", "ucc", "wri", "emu", "eit",
"xpe", "rgo", "lma", "ryn", "yno", "kee", "yel", "myc", "fat", "udg", "sai", "isl", "uer", "lca", "syl", "vab", "eom", "sex", "mad", "oor",
"ild", "nmo", "nuc", "auc", "lmi", "upt", "sip", "ify", "bos", "xte", "sno", "kno", "teg", "eye", "kli", "uls", "way", "eot", "rpi", "osu",
"gol", "fou", "now", "eon", "iev", "dot", "sun", "zon", "obr", "ced", "fea", "ilt", "ygo", "oki", "bun", "tau", "eog", "cya", "uca", "rtr",
"nau", "uid", "mpu", "uga", "lme", "phl", "moi", "ltu", "cup", "lmo", "lud", "sei", "lva", "nip", "irt", "ray", "yar", "etu", "sot", "cty",
"iba", "nho", "spu", "ues", "ogu", "mip", "nov", "gab", "olv", "rpa", "mig", "uru", "buc", "nva", "asu", "mim", "mem", "twa", "enl", "dru",
"nun", "izo", "urc", "rae", "idu", "gru", "rwo", "cca", "mom", "inh", "apr", "sad", "poo", "kar", "jud", "thm", "swo", "fib", "urp", "pes",
"nwa", "nvo", "eem", "ync", "him", "cht", "yng", "ubr", "lta", "beg", "cav", "efr", "uel", "utc", "pee", "rbu", "mec", "nut", "lub", "oet",
"ebi", "dys", "ned", "tow", "iag", "lco", "imu", "hib", "mam", "yne", "npo", "vie", "gyn", "ptu", "lgi", "rhy", "tep", "zab", "chm", "cov",
"rlo", "gos", "mab", "ulc", "bow", "vac", "aze", "ium", "ips", "isu", "esq", "url", "cei", "vou", "rfi", "fru", "lly", "rui", "odd", "bod",
"fig", "adu", "iam", "adj", "had", "ulg", "rox", "cka", "tyr", "hoi", "eap", "rno", "nym", "orl", "aen", "fia", "kal", "nla", "rsp", "omn",
"anl", "ird", "yis", "fid", "kit", "upi", "hma", "ift", "vet", "raz", "tak", "hoe", "rfu", "kab", "ofi", "cun", "uir", "aes", "bid", "xan",
"ndy", "deo", "bie", "nsm", "hli", "niv", "lov", "pog", "irm", "rwa", "bum", "hig", "luo", "eha", "lew", "dai", "igm", "thl", "slu", "dib",
"lsi", "ney", "gib", "nei", "nsl", "nju", "shm", "ipr", "dus", "bse", "lpi", "xid", "nnu", "gir", "xyl", "tob", "aya", "zar", "dir", "rpl",
"dee", "idl", "jur", "jun", "dmi", "ask", "cub", "sod", "ifl", "nbo", "enf", "edd", "bee", "urv", "zer", "ghe", "new", "ppa", "cob", "oya",
"arv", "ryt", "exu", "ntn", "orh", "lci", "nkl", "hno", "two", "nav", "nds", "inq", "coh", "ees", "ows", "oru", "ucl", "gom", "goo", "uet",
"nef", "wai", "cch", "liq", "viv", "kat", "oal", "owa", "phu", "muc", "pad", "rdl", "rtl", "hus", "pep", "fit", "hni", "ypi", "otu", "nwo",
"olt", "bag", "obu", "elm", "iap", "wle", "nba", "gog", "ilu", "amu", "gme", "try", "idg", "utb", "zen", "yni", "oit", "als", "agl", "amy",
"civ", "wag", "eag", "urd", "ymi", "dag", "onb", "doo", "cib", "ynt", "cic", "sav", "yal", "bib", "fai", "gyr", "oec", "acl", "ofe", "xin", 
"zzl", "emm", "dun", "usp", "xpl", "vig", "opy", "tei", "nbr", "dod", "osy", "pru", "pso", "smu", "mio", "nri", "mif", "isr", "fet", "hyo",
"uvi", "isf", "uph", "pio", "yop", "lul", "reu", "rak", "isb", "ubo", "iki", "quo", "ohy", "lil", "izz", "ubd", "irl", "mob", "aid", "how",
"eps", "egl", "uor", "npl", "nra", "dei", "neb", "poe", "apu", "aka", "sog", "isk", "oas", "dyl", "deg", "oub", "oup", "lge", "utl", "rmu",
"iec", "sni", "ubm", "lol", "lvi", "dve", "ahe", "elp", "fee", "eau", "kne", "wha", "wei", "tst", "ubp", "nro", "jus", "shu", "adl", "ipu",
"giz", "nae", "oxa", "kan", "pau", "nbu", "aet", "nua", "haw", "teu", "deh", "etc", "tup", "ory", "xic", "boi", "yso", "nwi", "law", "nmi",
"lef", "dua", "onj", "myr", "erk", "eup", "igl", "zyg", "onh", "mne", "lfi", "agm", "tad", "suf", "inl", "rex", "iid", "eus", "hry", "pab",
"bai", "ioi", "gun", "icy", "rfo", "itl", "cow", "loq", "aim", "odl", "siz", "das", "gga", "stm", "rug", "sos", "hoa", "eec", "ayi", "key",
"anh", "xer", "rgu", "cqu", "tla", "yre", "fam", "utu", "eir", "eid", "bef", "ifu", "ety", "toe", "pub", "ups", "aha", "aty", "ngs", "ttr",
"yse", "req", "toa", "nev", "sui", "big", "sir", "gau", "mno", "xen", "gum", "ckb", "ecc", "hti", "rfa", "yon", "tuo", "lch", "hwa", "rfl",
"twe", "ley", "hif", "rcl", "amn", "arf", "gad", "laz", "lga", "rah", "rbr", "egg", "isg", "joi", "npu", "wne", "bje", "tus", "dwa", "hav",
"dma", "seq", "gai", "xil", "roh", "bom", "utw", "nlo", "oof", "dap", "ymb", "hfu", "isd", "eek", "rej", "rgr", "tsh", "pae", "hak", "mye",
"acq", "liu", "ffr", "pst", "rpu", "enh", "pno", "lka", "ibo", "utp", "rbl", "odr", "nzo", "aig", "irs", "rok", "eod", "yer", "oxe", "dst",
"bif", "who", "fug", "upa", "urf", "noe", "lfu", "eex", "bba", "atl", "ofl", "lui", "mud", "ads", "awn", "bso", "goi", "rwi", "etl", "aea",
"exh", "rym", "wra", "pus", "bco", "rur", "dgi", "lbu", "lal", "sob", "soi", "elf", "oic", "cyl", "kma", "usk", "aru", "ryl", "psa", "elv",
"gus", "dju", "enr", "sew", "umu", "erj", "asy", "bsc", "bad", "awl", "jug", "eim", "thw", "ymn", "lbo", "fau", "lyz", "god", "soa", "mov",
"hyg", "eiv", "wes", "ffu", "voi", "stf", "utf", "noo", "els", "rue", "zoi", "ckw", "zan", "moe", "gid", "ryi", "myt", "vap", "omu", "uic",
"aed", "nsf", "sev", "dhe", "ryg", "rqu", "ldo", "mna", "owd", "rwe", "rcr", "rdr", "aza", "ggr", "alf", "ulm", "rry", "unu", "egm", "fem",
"afe", "idn", "abd", "eaf", "ebl", "axo", "nyl", "owb", "haf", "nsy", "nwe", "cim", "gha", "wli", "otc", "jou", "oer", "sas", "eju", "uge",
"eil", "wir", "ysa", "lpa", "kha", "ady", "gop", "gag", "sys", "urm", "xpo", "wre", "ubu", "ogy", "awe", "nsw", "axe", "osh", "lki", "day",
"eny", "lth", "bry", "hmi", "roz", "fix", "lto", "fum", "yot", "saf", "bem", "oud", "lyg", "lak", "thn", "hut", "ovo", "sug", "mle", "kni",
"teb", "ixe", "seg", "leh", "dum", "rnu", "ely", "doe", "chw", "bog", "mys", "oes", "loy", "ngt", "lyi", "luv", "yps", "ccl", "kst", "emn",
"ied", "ypn", "ska", "soo", "bdo", "nbl", "doi", "fon", "uie", "deu", "ckn", "aum", "ehy", "ygi", "loe", "hru", "box", "vot", "ngh", "zie",
"dew", "fti", "wab", "ucu", "cai", "doa", "seb", "eez", "env", "adh", "hya", "inb", "dex", "atm", "gho", "bew", "lke", "lst", "roy", "hiv",
"xio", "fab", "iad", "oho", "gut", "lbe", "maz", "xce", "iaz", "nhu", "iur", "ilm", "cae", "ifa", "tui", "rsc", "ums", "pup", "iru", "yge",
"mau", "yes", "onk", "uve", "eki", "uzz", "tym", "eev", "duo", "mix", "beh", "lax", "eke", "cne", "pow", "dox", "max", "ffo", "dau", "dsh",
"cof", "inw", "tav", "zol", "gig", "rik", "cyp", "rvo", "eob", "ixi", "tba", "otl", "tok", "nak", "lfa", "npi", "nyc", "oft", "ugu", "yca",
"lda", "uas", "zle", "ats", "boc", "wak", "tfi", "lya", "bob", "ols", "yte", "sre", "nus", "nhi", "hbo", "aec", "xon", "yos", "ilv", "onw",
"lug", "giv", "oye", "vai", "kho", "oze", "arw", "pay", "odg", "ged", "nib", "ulv", "heb", "cyn", "ugi", "dho", "ved", "uam", "dup", "dwo",
"xpr", "idr", "ndw", "xim", "ypa", "bud", "pok", "ohi", "ams", "bsi", "uab", "gym", "aic", "kir", "hyc", "jar", "bte", "obt", "lba", "xat",
"gst", "vea", "boy", "you", "hmo", "hul", "taf", "lcu", "ofa", "wid", "unj", "ruf", "awi", "rgl", "oam", "hwo", "lfo", "saw", "tae", "lir",
"umn", "sef", "llb", "xia", "epp", "mah", "fav", "tbo", "zym", "tef", "hyn", "ury", "yab", "any", "usu", "nbi", "awk", "llm", "ika", "nmu",
"ffa", "vei", "rax", "toz", "xci", "olp", "ckm", "dwi", "dmo", "map", "thf", "lsh", "kes", "jer", "idy", "lbi", "obj", "njo", "tew", "elc",
"pim", "nka", "ilk", "tmo", "xis", "neq", "lux", "cag", "enw", "hev", "tex", "pug", "dob", "azz", "nru", "ksh", "fut", "nir", "sbe", "etw",
"vem", "uld", "cui", "eei", "kon", "icc", "tiu", "dme", "xam", "pav", "tsi", "fei", "giu", "bug", "kam", "mae", "mst", "rii", "mli", "haz",
"lwo", "olf", "nhy", "gob", "meo", "xac", "iob", "orw", "eio", "xti", "zoa", "feu", "lse", "tpr", "jam", "oog", "pei", "mee", "pud", "ymm",
"irk", "eje", "tsm", "ghi", "unq", "fos", "ndb", "hov", "wif", "ndm", "myx", "wro", "cko", "bak", "ypr", "goe", "vib", "imn", "tof", "wni",
"ywo", "nof", "heu", "ucr", "oag", "ofo", "zot", "wig", "ynd", "stp", "miu", "ubj", "ckh", "btr", "fed", "sba", "oka", "sfu", "aym", "icn",
"uip", "oem", "yol", "myl", "xpa", "vad", "ief", "wad", "ity", "snu", "htl", "rdu", "oha", "hay", "lsa", "may", "stc", "hiu", "foc", "bui",
"utg", "lyo", "kwa", "inm", "tah", "tuf", "irg", "ews", "kna", "ths", "bip", "yac", "xcu", "xit", "mps", "chb", "rtm", "xal", "bap", "ked",
"eun", "lpe", "pik", "ttu", "afi", "pyl", "kra", "urk", "rey", "nze", "ubf", "dha", "cym", "bde", "ydo", "hew", "nub", "bdi", "ihe", "kag",
"hla", "aho", "psu", "ntw", "ndf", "ptr", "oxo", "inj", "sky", "gaz", "ckf", "goa", "wol", "rty", "ots", "uai", "jan", "lup", "yda", "ssm",
"peu", "orf", "oap", "lso", "gap", "rts", "lyb", "kro", "wic", "mok", "psh", "ciu", "uke", "yba", "ilb", "tuc", "yog", "yci", "ods", "aml",
"uiv", "tsc", "dsm", "ieg", "xtu", "fly", "yga", "tsp", "jin", "ayl", "rds", "dae", "sfi", "egn", "vec", "say", "ytr", "bau", "nox", "olk",
"dyi", "stn", "ecy", "uso", "kwo", "bbo", "pum", "won", "zop", "lyr", "yom", "pyo", "eif", "dry", "nlu", "aux", "pif", "uea", "utd", "gmo",
"uac", "tbr", "ils", "eyi", "axa", "ckt", "cak", "ygr", "peo", "uee", "anz", "aby", "rct", "aub", "ppu", "ngb", "ioe", "isy", "anq", "yoc",
"rkl", "lwa", "gee", "kol", "foi", "rya", "huc", "ulk", "cef", "tca", "kid", "rek", "seo", "wav", "syr", "lua", "ugl", "ubv", "wen", "gov",
"sfo", "bim", "uot", "oir", "ezi", "goc", "dfi", "ckp", "taw", "xua", "toh", "eya", "nts", "kor", "scy", "kto", "nph", "pma", "pam", "kyl",
"wed", "sof", "owt", "ayo", "syp", "hok", "lyn", "enb", "nwr", "tov", "uod", "ajo", "kla", "lce", "ysh", "yde", "upb", "mih", "xec", "xem",
"ofu", "eze", "ahi", "owm", "joy", "yea", "ael", "pme", "olc", "oeb", "cec", "dbo", "tfo", "sgr", "ozi", "ywa", "ncy", "ntm", "eof", "chp",
"nje", "yho", "naf", "ims", "enm", "ooi", "gae", "aup", "peg", "stw", "eoi", "gei", "khe", "kul", "yct", "gim", "tyc", "wam", "yod", "lpo",
"ooz", "yxo", "tse", "oum", "cuc", "viz", "lho", "dda", "nud", "arh", "cud", "rao", "uxi", "mei", "llf", "uag", "sud", "ctl", "chs", "dfu", 
"ihy", "dym", "ysp", "eug", "tso", "kad", "wie", "xor", "buf", "zze", "rnm", "mug", "elb", "eft", "eef", "aor", "dov", "heg", "dso", "ssh",
"caf", "omf", "vip", "nsk", "jum", "rfr", "ooc", "sov", "lhe", "nuf", "yrr", "htf", "iew", "ryc", "oko", "eeb", "dba", "ako", "sts", "adw",
"rsl", "exs", "bei", "zzi", "bdu", "dad", "bam", "tco", "olm", "bep", "ulb", "nny", "sbo", "llw", "ybe", "hug", "fog", "dla", "itm", "esb",
"lls", "inp", "ihi", "cua", "rka", "isq", "tfa", "pyg", "eiz", "lgo", "oei", "uty", "itz", "ngw", "oyi", "shw", "ghb", "jor", "dvi", "ogm",
"joc", "xha", "bop", "sue", "vau", "upu", "ybo", "vok", "sut", "ssy", "vow", "tih", "jes", "ewh", "kia", "gac", "owh", "paw", "its", "shb",
"jol", "irp", "kbo", "eic", "esk", "pew", "kou", "ubg", "atf", "rua", "nui", "lox", "bve", "vey", "dwe", "due", "mib", "noa", "kai", "mum",
"kas", "naw", "pex", "tsa", "dlo", "ahu", "sde", "esw", "afr", "oed", "sfe", "nza", "xos", "xur", "ehi", "anf", "gwo", "ndh", "kew", "oep",
"oak", "dca", "ssw", "esl", "daw", "kep", "ayb", "bme", "tga", "dub", "ldr", "veg", "puc", "xop", "utm", "tpo", "bay", "upc", "gbo", "yor",
"kem", "kim", "oeo", "maj", "xot", "awb", "xag", "ioa", "ueb", "esy", "oie", "ybr", "tzi", "ugn", "tiq", "bju", "biu", "dfa", "agh", "nya",
"jap", "ccr", "uia", "tdo", "wma", "nzi", "pty", "ewr", "yss", "yen", "ets", "lof", "ckr", "yhe", "mew", "naz", "rhu", "yoi", "seh", "ooe",
"zel", "veo", "llh", "zza", "ceb", "euk", "ejo", "etm", "lyd", "sbu", "nkn", "roj", "arq", "uml", "onq", "ebb", "ldl", "tsu", "eaw", "nuo",
"nsn", "bub", "fad", "xcl", "fag", "xco", "iin", "tge", "cuo", "adn", "osk", "acy", "chf", "lfe", "wou", "cox", "iny", "vom", "ekn", "msh",
"gyp", "hik", "opn", "opm", "xip", "edb", "ixt", "ndp", "lro", "ayw", "ylu", "dva", "cah", "ngf", "moh", "wde", "odw", "kfu", "jag", "xto",
"hts", "ays", "byr", "kur", "rir", "piz", "eul", "bmi", "nja", "eia", "von", "tik", "nue", "xyg", "enj", "ilf", "wns", "zil", "gif", "isn",
"aik", "dse", "owp", "adf", "eds", "raj", "yat", "sfa", "upo", "tze", "ubh", "ysm", "elw", "sve", "meb", "zea", "rtn", "gth", "vam", "hua",
"tay", "ixa", "mfo", "asq", "ssb", "ilg", "sgu", "adg", "loh", "kip", "tlo", "sak", "wbe", "pwo", "rnl", "bne", "oex", "dik", "lds", "apy",
"geb", "cly", "rml", "sku", "uos", "moa", "zit", "atw", "alw", "pem", "stb", "gef", "keb", "tpa", "kic", "nty", "dav", "uef", "sow", "rdm",
"oje", "bae", "rks", "rju", "kea", "daz", "abn", "vif", "cew", "bma", "ehu", "etf", "xie", "zem", "ayf", "iem", "jel", "jet", "afo", "ntg",
"wax", "lha", "atb", "kfi", "rns", "tsw", "rlu", "afa", "bta", "chc", "kri", "aja", "isj", "uki", "rpt", "gav", "nsv", "rtw", "yze", "chd",
"fox", "bca", "yet", "apn", "rsw", "anj", "ssf", "jas", "sax", "zor", "aws", "sst", "xpi", "piu", "jew", "gmi", "ilc", "ily", "afl", "bpr",
"klo", "dsi", "noh", "eja", "yag", "siu", "wbo", "isw", "beb", "biv", "itn", "juv", "nyi", "ssp", "gew", "byt", "udl", "tbu", "bha", "hyb",
"soe", "xhi", "hub", "bti", "rgy", "wse", "eew", "nik", "onz", "alr", "uak", "tbe", "yok", "awf", "bbr", "hba", "wba", "xcr", "nvu", "inr",
"oui", "sko", "dof", "rsm", "oys", "pyc", "ezz", "cay", "mef", "tcr", "wsh", "bov", "jon", "xoc", "yam", "boe", "myd", "owf", "nih", "iko",
"irb", "kot", "luf", "vex", "lyh", "umv", "irn", "ngm", "fow", "oov", "bys", "ouv", "kre", "adb", "ydi", "ddo", "huf", "mya", "lyl", "nzy",
"myi", "aei", "pye", "wwo", "pfu", "pbo", "cee", "rih", "otm", "ieu", "lah", "bsu", "yta", "otw", "hwe", "thb", "lld", "zam", "aiv", "uiz",
"aia", "bno", "ysc", "ozy", "roq", "jut", "ahm", "oww", "oau", "yur", "uif", "aeg", "xod", "oig", "eys", "dpa", "rgh", "erq", "iho", "fta",
"eka", "gow", "mfu", "rdw", "ugo", "nup", "knu", "ipy", "ygm", "elh", "ghl", "uko", "auf", "bah", "iox", "yfu", "koo", "eyl", "wnl", "toy",
"ipm", "edf", "zli", "doz", "iov", "kop", "kap", "sdi", "lfl", "pyt", "bez", "xpu", "dsp", "hta", "shf", "otb", "tfl", "aje", "lye", "rdn",
"umf", "yfi", "tbl", "sht", "nii", "pef", "tth", "rnf", "gwa", "akh", "baw", "zom", "orv", "ahy", "hho", "msi", "mpy", "gdo", "geu", "lyw",
"mse", "oyl", "fuc", "ilp", "edw", "ubn", "okl", "coy", "ofr", "obv", "wac", "mro", "ezo", "uav", "lok", "ouf", "lwi", "ply", "eoe", "hox",
"lmu", "hna", "rja", "hud", "bev", "web", "pwa", "voy", "cni", "ndn", "nks", "ybu", "sby", "ozz", "gfi", "ycn", "dbe", "sdo", "hst", "rwh",
"tue", "gsh", "ksi", "awd", "jaw", "tty", "ldf", "jub", "nwh", "eoa", "tyi", "oua", "tys", "kom", "sra", "jig", "ivu", "niq", "wst", "bho",
"lln", "ohu", "waf", "yit", "itf", "wet", "ogh", "oms", "aku", "anb", "eym", "nug", "rtf", "dih", "ctn", "wau", "uxe", "biz", "esn", "uxo",
"iwa", "hui", "kya", "lii", "cuf", "cyr", "oue", "cue", "awo", "azu", "aiz", "ylv", "lgu", "xog", "ids", "wim", "uxu", "xta", "tya", "six",
"irw", "mso", "ddu", "kwi", "kbi", "wiv", "byl", "adc", "sro", "ilh", "llp", "evu", "ybi", "tyo", "atn", "kme", "anr", "gaw", "mik", "lpr",
"rye", "oio", "ilw", "bvi", "yzo", "xol", "uds", "cno", "rnb", "ksm", "ovu", "ywi", "nky", "etb", "pob", "xca", "yaw", "anw", "ugm", "dah",
"btu", "agy", "jul", "ggo", "ddr", "iae", "ftl", "zeb", "eea", "bey", "htn", "lks", "asl", "xab", "yem", "kta", "shp", "yza", "xom", "yas",
"luk", "oml", "imy", "job", "etn", "zoc", "kef", "elk", "iha", "hye", "iml", "enp", "gec", "yak", "viu", "ufa", "feb", "dfo", "foa", "jai",
"veh", "sda", "wki", "gip", "zet", "raq", "osl", "rje", "eox", "upf", "mez", "enk", "boh", "rld", "yrm", "kba", "ryb", "bcl", "aio", "ems",
"baz", "poa", "uny", "yrt", "ceu", "ltl", "gba", "fev", "ntf", "pez", "zir", "eyn", "mvi", "oax", "kso", "ygl", "ewl", "yxi", "ddy", "dek",
"loz", "lky", "oim", "odh", "ags", "kwe", "ldn", "abj", "eml", "hpo", "owc", "kok", "aev", "dja", "miv", "ayn", "dbu", "xib", "muf", "feo",
"anv", "buk", "kum", "euv", "xas", "iau", "lix", "hsh", "hwi", "rkm", "mep", "tbi", "iof", "pco", "sey", "aeu", "moz", "ixo", "tmi", "dbr",
"zog", "kib", "erz", "sbi", "kbe", "oev", "rdb", "tdr", "coz", "kke", "hey", "yru", "yfo", "tcl", "sva", "guo", "ifr", "nkt", "daf", "xiv",
"caj", "rwr", "xyc", "uem", "dvo", "zal", "dii", "fts", "cig", "ytt", "lvu", "owr", "ysu", "jos", "lfr", "ieb", "iwi", "tpi", "ccy", "upg",
"ymu", "wap", "oyo", "ldb", "nkf", "pue", "emy", "uqu", "wme", "tgr", "foe", "jau", "kun", "dna", "wsi", "std", "amf", "pbr", "nax", "gfu",
"ndc", "riq", "kru", "mfi", "oea", "xys", "ipn", "anm", "ngy", "eza", "egy", "uva", "dye", "ryw", "bpe", "kpo", "gda", "pki", "dth", "suo",
"cyg", "rze", "agb", "joh", "eyb", "ctf", "jab", "sru", "doh", "aol", "dyt", "akl", "sox", "adp", "eaz", "teh", "cyo", "lgr", "dui", "zap",
"twr", "hyi", "bch", "tev", "hpr", "eyo", "gey", "wip", "rmy", "nkh", "elr", "avu", "ckc", "zos", "coq", "tza", "dje", "idw", "umd", "noz",
"hhe", "geh", "kei", "wfu", "tsk", "ouk", "keh", "ipw", "hef", "uib", "hdr", "xed", "kio", "puf", "hbe", "ued", "oab", "rsk", "xig", "xar",
"yad", "itw", "dak", "myg", "noy", "btl", "oob", "djo", "asb", "fne", "pyi", "tpu", "wur", "lpt", "hbr", "gay", "aal", "kos", "ngd", "dbi",
"utv", "pox", "dfl", "msc", "nkr", "gbe", "zim", "upw", "opw", "yie", "pib", "sah", "yap", "deq", "laf", "eeh", "htm", "nah", "htw", "ilr",
"gto", "bej", "rtg", "dut", "llt", "ecd", "hah", "wfi", "odc", "bwo", "anp", "ywe", "auk", "rps", "kah", "edm", "odm", "osq", "pov", "iep",
"ksp", "nmy", "mho", "dga", "dno", "bpa", "ogw", "pah", "biq", "eip", "jib", "xch", "pbe", "usb", "zia", "ffs", "peb", "lwe", "tgu", "rdh",
"iya", "dud", "iex", "tni", "rdf", "gwi", "ahl", "rnw", "olg", "tpl", "upd", "akr", "rix", "dej", "eyf", "zeu", "lih", "raa", "umc", "wdi", 
"dgm", "iun", "buz", "vro", "tek", "kse", "akt", "uvr", "muz", "hco", "bek", "hof", "puk", "gnm", "azy", "wso", "kak", "gnu", "dsc", "adt",
"dco", "cok", "eyw", "yed", "skl", "iif", "zed", "ueu", "ssc", "uze", "ygd", "eik", "dpi", "oks", "ptl", "pry", "obd", "eov", "ldu", "hso",
"jea", "agw", "ogs", "ihu", "vog", "uka", "utn", "gub", "olh", "vos", "otp", "soh", "tug", "xif", "zzo", "oku", "oym", "aji", "buo", "uay",
"kif", "hue", "pca", "shy", "puz", "dya", "vee", "rsy", "wov", "ekt", "lvo", "ldw", "fue", "ufl", "abh", "jal", "uei", "yfl", "yha", "neh",
"wke", "olb", "nyw", "ndg", "bly", "baf", "mow", "ypl", "igs", "bby", "gep", "xyp", "owo", "lpl", "bfu", "thd", "gta", "ghh", "ogb", "wiz",
"amr", "tna", "hpa", "uft", "hfi", "chh", "awm", "jov", "rvu", "odb", "wla", "nyo", "ysy", "mwo", "ryd", "cku", "nys", "gwe", "uec", "fud",
"jit", "rtb", "ioh", "awt", "ayd", "jee", "hek", "koi", "udr", "tfe", "iax", "wye", "jim", "dpo", "xap", "aju", "jad", "iju", "eie", "ouz",
"jeo", "sik", "kaw", "ohn", "utz", "zur", "uoi", "ewd", "xyt", "sfr", "pda", "dpr", "fim", "kdo", "sga", "usq", "pwe", "gry", "ayt", "xho",
"dyp", "ghf", "elg", "thp", "kay", "maw", "dhi", "pth", "gyl", "apb", "llc", "ycu", "syb", "dch", "amw", "wah", "maf", "ftw", "rkn", "oiz",
"mnu", "hdo", "ekk", "nxi", "zid", "ckd", "piq", "nss", "kup", "yau", "bii", "alh", "jav", "obn", "bsp", "auv", "ayu", "yid", "hsi", "dgr",
"nkw", "ngk", "omt", "sok", "utj", "bfo", "fod", "ipb", "itb", "meu", "fif", "ipc", "kti", "ewt", "rko", "klu", "aar", "beq", "wbr", "stt",
"rkh", "apm", "irh", "zai", "lpu", "tyn", "gti", "ybl", "zig", "otf", "igw", "lbr", "sju", "bik", "cza", "eoz", "xul", "ufi", "moy", "nkb",
"uaw", "bcu", "yah", "jaz", "hbu", "apw", "pfl", "lra", "dyb", "yzi", "rnt", "irv", "kbu", "uio", "wth", "zad", "zip", "rcy", "hsa", "eeg",
"ybd", "bcr", "ldh", "gud", "lko", "few", "jil", "hpi", "thh", "ldm", "fak", "akk", "lms", "fov", "lri", "lsm", "jej", "mla", "nrh", "hii",
"kyr", "iry", "jok", "eyd", "pba", "jui", "bhe", "shk", "fuz", "bto", "hao", "uku", "oeu", "chk", "hoz", "msp", "tii", "rly", "xad", "lyf",
"owg", "rij", "uig", "aks", "ija", "sek", "dti", "psp", "pmo", "etz", "udu", "ruo", "skr", "evr", "dcr", "ebt", "yfa", "oxb", "eoh", "bok",
"faw", "sgo", "bda", "kpi", "htr", "kmo", "tcu", "jen", "ksc", "ylp", "nkm", "ghm", "ewb", "duk", "rtz", "sae", "cye", "sjo", "nko", "isv",
"acm", "jay", "wdl", "lcy", "ddh", "mfe", "tki", "byi", "tda", "myz", "ojo", "awh", "uep", "bfi", "ypu", "mek", "lsu", "umr", "bge", "wdr",
"ftm", "ltm", "laq", "vef", "wto", "ogt", "pbu", "kbr", "esd", "ugr", "kus", "iln", "oou", "mlo", "vim", "edc", "awy", "pui", "wob", "ltz",
"lsp", "llr", "lkl", "cyb", "sgi", "oik", "ozl", "bpo", "hup", "syi", "duf", "mav", "rjo", "ksl", "ksa", "ufo", "tpe", "nay", "zoe", "ptn",
"rtc", "ngp", "bye", "odp", "pey", "xya", "thc", "rza", "caw", "ubw", "oef", "ekl", "axw", "ijo", "ayg", "xah", "heq", "wnw", "lek", "uol",
"ayr", "hoy", "rnp", "ukk", "wdo", "iez", "jow", "igy", "kod", "epf", "nps", "iit", "uxa", "eyr", "ivv", "iek", "hto", "dhu", "spy", "epw",
"ceg", "pwi", "koe", "dbl", "pdo", "yuc", "ywr", "bbu", "lkw", "edh", "wnt", "urw", "zep", "kaf", "pax", "lcr", "xyb", "awr", "pfi", "xyr",
"tyf", "inx", "dpl", "ynu", "pak", "fio", "yrh", "zul", "yke", "ndt", "nsg", "gsm", "anx", "rkw", "arz", "tdi", "mra", "rdc", "dsa", "liw",
"aue", "bsa", "ygn", "keo", "stv", "sdr", "rez", "stg", "omr", "oxh", "nsq", "urh", "aef", "bmo", "dsw", "uza", "atz", "dok", "opf", "lbl",
"vew", "uae", "aie", "ycr", "buy", "odf", "ytu", "wca", "wpo", "aou", "idh", "ogf", "gbi", "dsl", "ycy", "taz", "khi", "tvo", "jog", "poh",
"pch", "yox", "ewf", "pgr", "asw", "nxe", "cdo", "baa", "tht", "iei", "dez", "yeb", "miq", "kfa", "ruk", "phn", "mbs", "sge", "chg", "vur",
"rmf", "piv", "soz", "nku", "okr", "mwi", "fma", "sbr", "ghs", "ghw", "eui", "hfa", "opb", "whu", "bof", "kao", "shc", "ppy", "oaf", "kth",
"ywh", "ewm", "zif", "oia", "dyk", "zod", "aip", "tuk", "oeh", "idm", "olw", "iku", "uop", "ylg", "epm", "mwa", "zou", "xma", "mfl", "hch",
"ooh", "cky", "rsn", "oae", "irf", "xse", "oxc", "yae", "ziz", "ayc", "koc", "uev", "unz", "goy", "dcu", "iwe", "pni", "ius", "ibs", "bna",
"tja", "eow", "oyn", "aot", "bhu", "uoy", "tno", "wte", "aep", "ctm", "eae", "lni", "exf", "uvu", "wsm", "wsp", "uom", "lts", "ntz", "tey",
"ssr", "oyd", "urz", "acn", "gya", "utk", "yls", "rnc", "tgl", "eln", "lfh", "wof", "oif", "lna", "dey", "ghn", "fto", "ayp", "upm", "aib",
"agd", "jat", "pfo", "wow", "tqu", "rdy", "hda", "rnn", "ipf", "yof", "eyp", "maa", "zec", "uim", "mmy", "woe", "kka", "yew", "tiw", "iby",
"hoh", "edy", "llg", "orz", "iis", "uon", "kut", "yoh"
        };

        private static readonly Dictionary<char, string> Homoglyphs = new Dictionary<char, string> {
    { 'a', "A₳卂@4ΑАᎪᗅᴀꓮꭺＡ𐊠𖽀𝐀𝐴𝑨𝒜𝓐𝔄𝔸𝕬𝖠𝗔𝘈𝘼𝙰𝚨𝛢𝜜𝝖𝞐aɑαа⍺ａ𝐚𝑎𝒂𝒶𝓪𝔞𝕒𝖆𝖺𝗮𝘢𝙖𝚊𝛂𝛼𝜶𝝰𝞪" },
    { 'b', "B乃฿₿bʙɓ6bƄЬᏏᑲᖯｂ𝐛𝑏𝒃𝒷𝓫𝔟𝕓𝖇𝖻𝗯𝘣𝙗𝚋ʙΒВвᏴᏼᗷᛒℬꓐꞴＢ𐊂𐊡𐌁𝐁𝐵𝑩𝓑𝔅𝔹𝕭𝖡𝗕𝘉𝘽𝙱𝚩𝛣𝜝𝝗𝞑" },
    { 'c', "C匚⟨⟪⟮₵¢ɔ<[{(ccϲсᴄⅽⲥꮯｃ𐐽𝐜𝑐𝒄𝒸𝓬𝔠ς𝕔𝖈𝖼𝗰𝘤𝙘𝚌ϹСᏟᑕℂℭⅭ⊂Ⲥ⸦ꓚＣ𐊢𐌂𐐕𐔜𑣩𑣲𝐂𝐶𝑪𝒞𝓒𝕮𝖢𝗖𝘊𝘾𝙲🝌" },
    { 'd', "DᗪĐ0OdԁᏧᑯⅆⅾꓒｄ𝐝𝑑𝒅𝒹𝓭𝔡𝕕𝖉𝖽𝗱𝘥𝙙𝚍ᎠᗞᗪᴅⅅⅮꓓꭰＤ𝐃𝐷𝑫𝒟𝓓𝔇𝔻𝕯𝖣𝗗𝘋𝘿𝙳" },
    { 'e', "E乇3eеҽ℮ℯⅇꬲｅ𝐞𝑒𝒆𝓮𝔢𝕖𝖊𝖾𝗲𝘦𝙚𝚎ΕЕᎬᴇℰ⋿ⴹꓰꭼＥ𐊆𑢦𑢮𝐄𝐸𝑬𝓔𝔈𝔼𝕰𝖤𝗘𝘌𝙀𝙴𝚬𝛦𝜠𝝚𝞔" },
    { 'f', "F千fſϝքẝꞙꬵｆ𝐟𝑓𝒇𝒻𝓯𝔣𝕗𝖋𝖿𝗳𝘧𝙛𝚏𝟋ϜᖴℱꓝꞘＦ𐊇𐊥𐔥𑢢𑣂𝈓𝐅𝐹𝑭𝓕𝔉𝔽𝕱𝖥𝗙𝘍𝙁𝙵𝟊" },
    { 'g', "GᎶgƍɡցᶃℊｇ𝐠𝑔𝒈𝓰𝔤𝕘𝖌𝗀𝗴𝘨𝙜𝚐ɢԌԍᏀᏳᏻꓖꮐＧ𝐆𝐺𝑮𝒢𝓖𝔊𝔾𝕲𝖦𝗚𝘎𝙂𝙶" },
    { 'h', "HⱧ卄hһհᏂℎｈ𝐡𝒉𝒽𝓱𝔥𝕙𝖍𝗁𝗵𝘩𝙝𝚑ʜΗНнᎻᕼℋℌℍⲎꓧꮋＨ𐋏𝐇𝐻𝑯𝓗𝕳𝖧𝗛𝘏𝙃𝙷𝚮𝛨𝜢𝝜𝞖" },
    { 'i', "I丨ł1il|ıƖǀɩɪ˛ͺΙιІіӀӏ׀וןا١۱ߊᎥᛁιℐℑℓℹⅈⅠⅰⅼ∣⍳⏽Ⲓⵏꓲꙇꭵﺍﺎ１Ｉｉｌ￨𐊊𐌉𐌠𑣃𖼨𝐈𝐢𝐥𝐼𝑖𝑙𝑰𝒊𝒍𝒾𝓁𝓘𝓲𝓵𝔦𝔩𝕀𝕚𝕝𝕴𝖎𝖑𝖨𝗂𝗅𝗜𝗶𝗹𝘐𝘪𝘭𝙄𝙞𝙡𝙸𝚒𝚕𝚤𝚰𝛊𝛪𝜄𝜤𝜾𝝞𝝸𝞘𝞲𝟏𝟙𝟣𝟭𝟷𞣇𞸀𞺀🯱" },
    { 'j', "Jﾌ7jϳјⅉｊ𝐣𝑗𝒋𝒿𝓳𝔧𝕛𝖏𝗃𝗷𝘫𝙟𝚓ͿЈᎫᒍᴊꓙꞲꭻＪ𝐉𝐽𝑱𝒥𝓙𝔍𝕁𝕵𝖩𝗝𝘑𝙅𝙹" },
    { 'k', "K₭kҜｋ𝐤𝑘𝒌𝓀𝓴𝔨𝕜𝖐𝗄𝗸𝘬𝙠𝚔ΚКᏦᛕKⲔꓗＫ𐔘𝐊𝐾𝑲𝒦𝓚𝔎𝕂𝕶𝖪𝗞𝘒𝙆𝙺𝚱𝛫𝜥𝝟𝞙" },
    { 'l', "LㄥⱠʟᏞᒪℒⅬⳐⳑꓡꮮＬ𐐛𐑃𐔦𑢣𑢲𖼖𝈪𝐋𝐿𝑳𝓛𝔏𝕃𝕷𝖫𝗟𝘓𝙇𝙻1Iil|ıƖǀɩɪ˛ͺΙιІіӀӏ׀וןا١۱ߊᎥᛁιℐℑℓℹⅈⅠⅰⅼ∣⍳⏽Ⲓⵏꓲꙇꭵﺍﺎ１Ｉｉｌ￨𐊊𐌉𐌠𑣃𖼨𝐈𝐢𝐥𝐼𝑖𝑙𝑰𝒊𝒍𝒾𝓁𝓘𝓲𝓵𝔦𝔩𝕀𝕚𝕝𝕴𝖎𝖑𝖨𝗂𝗅𝗜𝗶𝗹𝘐𝘪𝘭𝙄𝙞𝙡𝙸𝚒𝚕𝚤𝚰𝛊𝛪𝜄𝜤𝜾𝝞𝝸𝞘𝞲𝟏𝟙𝟣𝟭𝟷𞣇𞸀𞺀🯱" },
    { 'm', "Mm爪₥ｍΜϺМᎷᗰᛖℳⅯⲘꓟＭ𐊰𐌑𝐌𝑀𝑴𝓜𝔐𝕄𝕸𝖬𝗠𝘔𝙈𝙼𝚳𝛭𝜧𝝡𝞛" },
    { 'n', "N几₦nոռｎ𝐧𝑛𝒏𝓃𝓷𝔫𝕟𝖓𝗇𝗻𝘯𝙣𝚗ɴΝℕⲚꓠＮ𐔓𝐍𝑁𝑵𝒩𝓝𝔑𝕹𝖭𝗡𝘕𝙉𝙽𝚴𝛮𝜨𝝢𝞜" },
    { 'o', "OㄖØ0OoΟοσОоՕօסه٥ھہە۵߀०০੦૦ଠ୦௦ం౦ಂ೦ംഠ൦ං๐໐ဝ၀ჿዐᴏᴑℴⲞⲟⵔ〇ꓳꬽﮦﮧﮨﮩﮪﮫﮬﮭﻩﻪﻫﻬ０Ｏｏ𐊒𐊫𐐄𐐬𐓂𐓪𐔖𑓐𑢵𑣈𑣗𑣠𝐎𝐨𝑂𝑜𝑶𝒐𝒪𝓞𝓸𝔒𝔬𝕆𝕠𝕺𝖔𝖮𝗈𝗢𝗼𝘖𝘰𝙊𝙤𝙾𝚘𝚶𝛐𝛔𝛰𝜊𝜎𝜪𝝄𝝈𝝤𝝾𝞂𝞞𝞸𝞼𝟎𝟘𝟢𝟬𝟶𞸤𞹤𞺄🯰" },
    { 'p', "P卩₱9pρϱр⍴ⲣｐ𝐩𝑝𝒑𝓅𝓹𝔭𝕡𝖕𝗉𝗽𝘱𝙥𝚙𝛒𝛠𝜌𝜚𝝆𝝔𝞀𝞎𝞺𝟈ΡРᏢᑭᴘᴩℙⲢꓑꮲＰ𐊕𝐏𝑃𝑷𝒫𝓟𝔓𝕻𝖯𝗣𝘗𝙋𝙿𝚸𝛲𝜬𝝦𝞠" },
    { 'q', "QQҨ9ℚqԛգզｑ𝐪𝑞𝒒𝓆𝓺𝔮𝕢𝖖𝗊𝗾𝘲𝙦𝚚ⵕＱ𝐐𝑄𝑸𝒬𝓠𝔔𝕼𝖰𝗤𝘘𝙌𝚀" },
    { 'r', "R尺Ɽ7Ʀrгᴦⲅꭇꭈꮁｒ𝐫𝑟𝒓𝓇𝓻𝔯𝕣𝖗𝗋𝗿𝘳𝙧𝚛ʀᎡᏒᖇᚱℛℜℝꓣꭱꮢＲ𐒴𖼵𝈖𝐑𝑅𝑹𝓡𝕽𝖱𝗥𝘙𝙍𝚁" },
    { 's', "S丂₴53Ѕsƽѕꜱꮪｓ𐑈𑣁𝐬𝑠𝒔𝓈𝓼𝔰𝕤𝖘𝗌𝘀𝘴𝙨𝚜ՏᏕᏚꓢＳ𐊖𐐠𖼺𝐒𝑆𝑺𝒮𝓢𝔖𝕊𝕾𝖲𝗦𝘚𝙎𝚂" },
    { 't', "Tㄒ₮7ΤτТtｔ𝐭𝑡𝒕𝓉𝓽𝔱𝕥𝖙𝗍𝘁𝘵𝙩𝚝тᎢᴛ⊤⟙ⲦꓔꭲＴ𐊗𐊱𐌕𑢼𖼊𝐓𝑇𝑻𝒯𝓣𝔗𝕋𝕿𝖳𝗧𝘛𝙏𝚃𝚻𝛕𝛵𝜏𝜯𝝉𝝩𝞃𝞣𝞽🝨" },
    { 'u', "UɄㄩ¿⏑ŪūǓǔŬŭÚúÛûŨũŰűüÜùÙǕǖǛǜʉǗǘŮůʋǙǚŲųʊVvuʋυսᴜꞟꭎꭒｕ𐓶𑣘𝐮𝑢𝒖𝓊𝓾𝔲𝕦𝖚𝗎𝘂𝘶𝙪𝚞𝛖𝜐𝝊𝞄𝞾Սሀᑌ∪⋃ꓴＵ𐓎𑢸𖽂𝐔𝑈𝑼𝒰𝓤𝔘𝕌𝖀𝖴𝗨𝘜𝙐𝚄" },
    { 'v', "Vᐯvνѵטᴠⅴ∨⋁ꮩｖ𑜆𑣀𝐯𝑣𝒗𝓋𝓿𝔳𝕧𝖛𝗏𝘃𝘷𝙫𝚟𝛎𝜈𝝂𝝼𝞶Ѵ٧۷ᏙᐯⅤⴸꓦꛟＶ𐔝𑢠𖼈𝈍𝐕𝑉𝑽𝒱𝓥𝔙𝕍𝖁𝖵𝗩𝘝𝙑𝚅" },
    { 'w', "W山₩wɯѡԝաᴡꮃｗ𑜊𑜎𑜏𝐰𝑤𝒘𝓌𝔀𝔴𝕨𝖜𝗐𝘄𝘸𝙬𝚠ԜᎳᏔꓪＷ𑣦𑣯𝐖𝑊𝑾𝒲𝓦𝔚𝕎𝖂𝖶𝗪𝘞𝙒𝚆" },
    { 'x', "XӾx乂×хᕁᕽ᙮ⅹ⤫⤬⨯ｘ𝐱𝑥𝒙𝓍𝔁𝔵𝕩𝖝𝗑𝘅𝘹𝙭𝚡ΧХ᙭ᚷⅩ╳ⲬⵝꓫꞳＸ𐊐𐊴𐌗𐌢𐔧𑣬𝐗𝑋𝑿𝒳𝓧𝔛𝕏𝖃𝖷𝗫𝘟𝙓𝚇𝚾𝛸𝜲𝝬𝞦" },
    { 'y', "Y¥Ɏyɣㄚʏγуүყᶌỿℽꭚｙ𑣜𝐲𝑦𝒚𝓎𝔂𝔶𝕪𝖞𝗒𝘆𝘺𝙮𝚢𝛄𝛾𝜸𝝲𝞬ΥϒУҮᎩᎽⲨꓬＹ𐊲𑢤𖽃𝐘𝑌𝒀𝒴𝓨𝔜𝕐𝖄𝖸𝗬𝘠𝙔𝚈𝚼𝛶𝜰𝝪𝞤" },
    { 'z', "ZΖzⱫ乙ᴢꮓｚ𑣄𝐳𝑧𝒛𝓏𝔃𝔷𝕫𝖟𝗓𝘇𝘻𝙯𝚣ᏃℤℨꓜＺ𐋵𑢩𑣥𝐙𝑍𝒁𝒵𝓩𝖅𝖹𝗭𝘡𝙕𝚉𝚭𝛧𝜡𝝛𝞕" },
        };

        private static List<TextElementInfo> BuildTextElements(string text)
        {
            var elements = new List<TextElementInfo>();
            var enumerator = StringInfo.GetTextElementEnumerator(text);
            while (enumerator.MoveNext())
            {
                elements.Add(new TextElementInfo
                {
                    Value = enumerator.GetTextElement(),
                    CharIndex = enumerator.ElementIndex
                });
            }
            return elements;
        }

        private static bool CheckHomoglyph(char expected, string textElement)
        {
            if (textElement.Length == 1 && char.ToLowerInvariant(expected) == char.ToLowerInvariant(textElement[0]))
            {
                return true;
            }
            if (Homoglyphs.TryGetValue(expected, out var homoglyphList))
            {
                return homoglyphList.Contains(textElement);
            }
            return false;
        }

        private static bool IsValidTrigram(string trigram, string bannedWord)
        {
            if (trigram.Length != 3) return false;

            // Skip if all trigram chars are also in the banned word.
            // This prevents the trigram check from blocking detection of the banned word itself.
            bool allInBannedWord = true;
            foreach (char c in trigram)
            {
                if (bannedWord.IndexOf(c) < 0)
                {
                    allInBannedWord = false;
                    break;
                }
            }
            if (allInBannedWord) return false;

            return Trigrams.Contains(trigram);
        }

        private static bool CheckTrigram(List<TextElementInfo> elements, int index, string bannedWord)
        {
            string current = elements[index].Value.ToLowerInvariant();

            // Forward trigram: [index, index+1, index+2]
            if (index + 2 < elements.Count)
            {
                string tri = current
                    + elements[index + 1].Value.ToLowerInvariant()
                    + elements[index + 2].Value.ToLowerInvariant();
                if (IsValidTrigram(tri, bannedWord)) return true;
            }

            // Backward trigram: [index-2, index-1, index]
            if (index >= 2)
            {
                string tri = elements[index - 2].Value.ToLowerInvariant()
                    + elements[index - 1].Value.ToLowerInvariant()
                    + current;
                if (IsValidTrigram(tri, bannedWord)) return true;
            }

            // Middle trigram: [index-1, index, index+1]
            if (index >= 1 && index + 1 < elements.Count)
            {
                string tri = elements[index - 1].Value.ToLowerInvariant()
                    + current
                    + elements[index + 1].Value.ToLowerInvariant();
                if (IsValidTrigram(tri, bannedWord)) return true;
            }

            return false;
        }

        /// <summary>
        /// After a match is found, checks if the match is embedded in a longer word
        /// by looking for valid trigrams crossing the match boundaries.
        /// Unlike the main trigram check, this does NOT skip trigrams whose characters
        /// are all in the banned word — that's the key to catching cases like "assassinate"
        /// where the banned word's characters (a,s) dominate the surrounding context.
        /// </summary>
        private static bool IsEmbeddedInWord(List<TextElementInfo> elements, List<int> matchedElementIndices)
        {
            if (matchedElementIndices.Count < 2) return false;

            int firstIdx = matchedElementIndices[0];
            int lastIdx = matchedElementIndices[matchedElementIndices.Count - 1];

            // Check end boundary: last 2 matched chars + next char after match
            if (lastIdx + 1 < elements.Count)
            {
                string nextEl = elements[lastIdx + 1].Value;
                if (nextEl.Length == 1 && char.IsLetter(nextEl[0]))
                {
                    string secondLast = elements[matchedElementIndices[matchedElementIndices.Count - 2]].Value;
                    string last = elements[lastIdx].Value;
                    string tri = (secondLast + last + nextEl).ToLowerInvariant();
                    if (tri.Length == 3 && Trigrams.Contains(tri))
                        return true;
                }
            }

            // Check start boundary: char before match + first 2 matched chars
            if (firstIdx > 0)
            {
                string prevEl = elements[firstIdx - 1].Value;
                if (prevEl.Length == 1 && char.IsLetter(prevEl[0]))
                {
                    string first = elements[matchedElementIndices[0]].Value;
                    string second = elements[matchedElementIndices[1]].Value;
                    string tri = (prevEl + first + second).ToLowerInvariant();
                    if (tri.Length == 3 && Trigrams.Contains(tri))
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Checks whether the text contains any banned word from the blacklist.
        /// Handles Unicode homoglyph substitution and character insertion evasion.
        /// Uses trigram analysis to avoid false positives (e.g. "ass" in "assignment").
        /// </summary>
        public static bool ContainsBannedWord(string text, string[] blacklist, out string matchedWord)
        {
            if (string.IsNullOrEmpty(text) || blacklist == null || blacklist.Length == 0)
            {
                matchedWord = "";
                return false;
            }

            var elements = BuildTextElements(text);

            foreach (var word in blacklist)
            {
                if (string.IsNullOrEmpty(word)) continue;

                int state = 0;
                var matchedElementIndices = new List<int>();
                for (int i = 0; i < elements.Count; i++)
                {
                    char target = word[state];
                    string element = elements[i].Value;

                    bool isHomoglyph = CheckHomoglyph(target, element);
                    bool isTrigram = CheckTrigram(elements, i, word);

                    if (isHomoglyph && !isTrigram)
                    {
                        matchedElementIndices.Add(i);
                        state++;
                    }
                    else if (isTrigram && state > 0)
                    {
                        matchedElementIndices.RemoveAt(matchedElementIndices.Count - 1);
                        state--;
                    }

                    if (state == word.Length)
                    {
                        if (!IsEmbeddedInWord(elements, matchedElementIndices))
                        {
                            matchedWord = word;
                            return true;
                        }
                        // Match was embedded in a longer word — reset and continue
                        state = 0;
                        matchedElementIndices.Clear();
                    }
                }
            }

            matchedWord = "";
            return false;
        }

        /// <summary>
        /// Filters a message by replacing banned word characters with asterisks.
        /// Handles Unicode homoglyph substitution and character insertion evasion.
        /// Uses trigram analysis to avoid false positives.
        /// </summary>
        public static string Filter(string text, string[] blacklist)
        {
            if (string.IsNullOrEmpty(text) || blacklist == null || blacklist.Length == 0)
                return text;

            char[] result = text.ToCharArray();

            foreach (var word in blacklist)
            {
                if (string.IsNullOrEmpty(word)) continue;

                bool foundMatch;
                do
                {
                    foundMatch = false;
                    var elements = BuildTextElements(new string(result));
                    int state = 0;
                    var matchedPositions = new List<TextElementInfo>();
                    var matchedElementIndices = new List<int>();

                    for (int i = 0; i < elements.Count; i++)
                    {
                        char target = word[state];
                        string element = elements[i].Value;

                        bool isHomoglyph = CheckHomoglyph(target, element);
                        bool isTrigram = CheckTrigram(elements, i, word);

                        if (isHomoglyph && !isTrigram)
                        {
                            matchedPositions.Add(elements[i]);
                            matchedElementIndices.Add(i);
                            state++;
                        }
                        else if (isTrigram && state > 0)
                        {
                            matchedPositions.RemoveAt(matchedPositions.Count - 1);
                            matchedElementIndices.RemoveAt(matchedElementIndices.Count - 1);
                            state--;
                        }

                        if (state == word.Length)
                        {
                            if (IsEmbeddedInWord(elements, matchedElementIndices))
                            {
                                // Match is part of a longer word — reset and continue
                                state = 0;
                                matchedPositions.Clear();
                                matchedElementIndices.Clear();
                                continue;
                            }

                            foreach (var pos in matchedPositions)
                            {
                                for (int j = pos.CharIndex; j < pos.CharIndex + pos.Value.Length && j < result.Length; j++)
                                {
                                    result[j] = '*';
                                }
                            }
                            foundMatch = true;
                            break;
                        }
                    }
                } while (foundMatch);
            }

            return new string(result);
        }
    }
}
