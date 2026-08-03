import sys, re, os
bad = ['—', '<b>', '</b>', '<i>', '</i>', '<para>', '</para>', 'rather than', 'exactly', 'deliberately', 'precisely', 'the whole', 'which is', 'for the same reason']
for p in sys.argv[1:]:
	for n, line in enumerate(open(p, encoding='utf-8-sig'), 1):
		s = line.strip()
		if not (s.startswith('//') or s.startswith('///')):
			continue
		for b in bad:
			if b in s:
				print(f"{os.path.basename(p)}:{n}: [{b}] {s}")
