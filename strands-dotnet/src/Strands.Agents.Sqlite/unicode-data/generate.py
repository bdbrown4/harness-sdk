import hashlib,json,re
from pathlib import Path
import argparse
parser=argparse.ArgumentParser(description='Regenerate the private Unicode17 QMD tables from explicitly supplied frozen normative files.')
parser.add_argument('--inputs',type=Path,required=True)
parser.add_argument('--output',type=Path,required=True)
args=parser.parse_args();GATE=Path(__file__).parent;INPUT=args.inputs;OUT=args.output.parent
H=lambda p:hashlib.sha256(p.read_bytes()).hexdigest().upper()
manifest=json.loads((GATE/'provenance.json').read_bytes())
for row in manifest['Inputs']:assert H(INPUT/row['Path'])==row['SHA256']
def merge(rows):
    result=[]
    for start,end in sorted(rows):
        if result and start<=result[-1][1]+1:result[-1]=(result[-1][0],max(end,result[-1][1]))
        else:result.append((start,end))
    return result
def interval(text):
    pieces=text.split('..');return int(pieces[0],16),int(pieces[-1],16)
words=[];spaces=[];lower={};pending=None
for line in (INPUT/'UnicodeData.txt').read_text(encoding='utf-8').splitlines():
    parts=line.split(';');cp=int(parts[0],16);category=parts[2]
    if parts[1].endswith(', First>'):assert pending is None;pending=(cp,category);continue
    if parts[1].endswith(', Last>'):
        assert pending is not None and pending[1]==category;start=pending[0];pending=None
    else:start=cp
    if category[0] in 'LN':words.append((start,cp))
    if category=='Zs':spaces.append((start,cp))
    if parts[13]:lower[cp]=(int(parts[13],16),)
assert pending is None
contexts=[]
for line in (INPUT/'SpecialCasing.txt').read_text(encoding='utf-8').splitlines():
    line=line.split('#',1)[0].strip()
    if not line:continue
    parts=[p.strip() for p in line.split(';')];cp=int(parts[0],16);mapping=tuple(int(p,16) for p in parts[1].split());condition=parts[4]
    if not condition:lower[cp]=mapping
    elif not any(re.fullmatch('[a-z]{2,3}',p) for p in condition.split()):contexts.append({'CodePoint':cp,'Mapping':mapping,'Condition':condition})
assert contexts==[{'CodePoint':0x03A3,'Mapping':(0x03C2,),'Condition':'Final_Sigma'}],contexts
lower={cp:target for cp,target in lower.items() if target!=(cp,)}
properties={'Cased':[],'Case_Ignorable':[]}
for line in (INPUT/'DerivedCoreProperties.txt').read_text(encoding='utf-8').splitlines():
    line=line.split('#',1)[0].strip()
    if not line:continue
    parts=[p.strip() for p in line.split(';')]
    if parts[1] in properties:properties[parts[1]].append(interval(parts[0]))
cjk=[]
for line in (INPUT/'Scripts.txt').read_text(encoding='utf-8').splitlines():
    line=line.split('#',1)[0].strip()
    if not line:continue
    points,script=[p.strip() for p in line.split(';')]
    if script in ['Han','Hiragana','Katakana','Hangul']:cjk.append(interval(points))
tables={'LetterNumber':merge(words),'CjkScript':merge(cjk),'Cased':merge(properties['Cased']),'CaseIgnorable':merge(properties['Case_Ignorable']),'EcmaWhitespace':merge(spaces+[(9,13),(0x2028,0x2029),(0xFEFF,0xFEFF)])}
single=[(cp,target[0]) for cp,target in sorted(lower.items()) if len(target)==1]
expansions={cp:target for cp,target in sorted(lower.items()) if len(target)!=1}
compressed=[];index=0
while index<len(single):
    start,target=single[index];delta=target-start;stride=1;end=start
    if index+1<len(single) and single[index+1][0]-start in [1,2] and single[index+1][1]-single[index+1][0]==delta:
        stride=single[index+1][0]-start
        while index+1<len(single) and single[index+1][0]-end==stride and single[index+1][1]-single[index+1][0]==delta:
            index+=1;end=single[index][0]
    compressed.append((start,end,stride,delta));index+=1
def number(value):return ('-0x'+format(-value,'X')) if value<0 else '0x'+format(value,'X')
def int_array(name,values):
    values=list(values);lines=[]
    for i in range(0,len(values),12):lines.append('        '+', '.join(number(v) for v in values[i:i+12])+',')
    return '    internal static readonly int[] '+name+' =\n    {\n'+'\n'.join(lines)+'\n    };\n'
def utf16hex(points):return ''.join(chr(cp) for cp in points).encode('utf-16-be').hex().upper()
source='// Generated Unicode 17.0.0 data. Copyright Unicode, Inc.\n// Distributed under Unicode License V3; see UnicodeLicenseV3.txt and unicode-data/provenance.json.\nnamespace Strands.Agents.Storage;\n\ninternal static class QmdUnicode17Data\n{\n'
for name,rows in tables.items():source+=int_array(name,[v for row in rows for v in row])
source+=int_array('LowerRanges',[v for row in compressed for v in row])
source+=int_array('LowerExpansionKeys',expansions.keys())
source+='    internal static readonly string[] LowerExpansionValues =\n    {\n'
for points in expansions.values():
    encoded=utf16hex(points);source+='        "'+''.join('\\u'+encoded[i:i+4] for i in range(0,len(encoded),4))+'",\n'
source+='    };\n}\n'
data_path=args.output;data_path.write_bytes(source.encode())
