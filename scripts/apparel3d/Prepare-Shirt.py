import pathlib, json, sys, numpy as np
from collections import defaultdict,Counter
from scipy.sparse import lil_matrix
from scipy.sparse.linalg import spsolve
repo=pathlib.Path(__file__).resolve().parents[2]
root=pathlib.Path(sys.argv[1]).resolve();root.mkdir(parents=True,exist_ok=True)
source_root=repo/'artwork/apparel3d'
source=source_root/'male_casualsuit06.obj'
v=[];uv=[];faces=[];fuv=[]
for line in source.read_text().splitlines():
 a=line.split()
 if not a:continue
 if a[0]=='v':v.append(list(map(float,a[1:4])))
 if a[0]=='vt':uv.append(list(map(float,a[1:3])))
 if a[0]=='f':
  faces.append([int(x.split('/')[0])-1 for x in a[1:]])
  fuv.append([int(x.split('/')[1])-1 for x in a[1:]])
v=np.array(v);uv=np.array(uv)
def components(polys,n):
 parent=list(range(n))
 def find(x):
  while parent[x]!=x:parent[x]=parent[parent[x]];x=parent[x]
  return x
 for f in polys:
  for j in f[1:]:parent[find(j)]=find(f[0])
 groups=defaultdict(list)
 for i,f in enumerate(polys):groups[find(f[0])].append(i)
 return sorted(groups.values(),key=len,reverse=True)
garments=components(faces,len(v))
shirt=max(garments,key=lambda ids:np.max([v[j,1] for i in ids for j in faces[i]]))
faceIds=shirt; faces=[faces[i] for i in shirt];fuv=[fuv[i] for i in shirt]
charts=components(fuv,len(uv));assert len(charts)==4
canonical=[None]*len(faces);parts=[None]*len(faces)
bodycharts=sorted(charts[:2],key=lambda ids:np.mean([v[j,2] for i in ids for j in faces[i]]),reverse=True)
for part,ids in enumerate(bodycharts):
 tex=np.array([uv[t] for i in ids for t in fuv[i]]); lo=tex.min(0);hi=tex.max(0)
 xs=np.array([v[j,0] for i in ids for j in faces[i]])
 corr=np.corrcoef(xs,tex[:,0])[0,1]
 flip=(corr<0) if part==0 else (corr>0)
 for i in ids:
  a=(uv[fuv[i]]-lo)/(hi-lo)
  a[:,1]=1-a[:,1]
  if flip:a[:,0]=1-a[:,0]
  canonical[i]=a.tolist();parts[i]=part
 print('Body',part,'faces',len(ids),'flip',flip,flush=True)
for ids in charts[2:]:
 # Weld equal UV coordinates: the OBJ duplicates indices around folded hems.
 tex=sorted(set(t for i in ids for t in fuv[i])); keys={};local={};points=[]
 for t in tex:
  key=tuple(uv[t]);k=keys.get(key)
  if k is None:k=len(points);keys[key]=k;points.append(uv[t])
  local[t]=k
 points=np.array(points);polys=[[local[t] for t in fuv[i]] for i in ids]
 edges=Counter(tuple(sorted((a,b))) for f in polys for a,b in zip(f,f[1:]+f[:1]))
 boundary=defaultdict(list);neighbors=defaultdict(set)
 for (a,b),count in edges.items():
  neighbors[a].add(b);neighbors[b].add(a)
  if count==1:boundary[a].append(b);boundary[b].append(a)
 loops=[];remaining=set(boundary)
 while remaining:
  start=next(iter(remaining));loop=[start];prev=-1
  for _ in range(len(boundary)+1):
   options=[x for x in boundary[loop[-1]] if x!=prev]
   if not options:break
   nxt=options[0]
   if nxt in loop:break
   prev=loop[-1];loop.append(nxt)
  remaining.difference_update(loop);loops.append(loop)
 loop=max(loops,key=len)
 corners=[]
 for n,j in enumerate(loop):
  a=points[loop[n-1]]-points[j];b=points[loop[(n+1)%len(loop)]]-points[j]
  angle=np.degrees(np.arccos(np.clip(a@b/np.linalg.norm(a)/np.linalg.norm(b),-1,1)))
  if angle<140:corners.append(j)
 assert len(corners)==4,('Sleeve corners',corners)
 bottom=sorted(corners,key=lambda j:points[j,1])[:2];top=[j for j in corners if j not in bottom]
 leftRoot,rightRoot=sorted(top,key=lambda j:points[j,0]);leftCuff,rightCuff=sorted(bottom,key=lambda j:points[j,0])
 anchors={leftRoot:(0,0),rightRoot:(0,1),leftCuff:(1,0),rightCuff:(1,1)}
 fixed={};ordered=sorted((loop.index(j),j) for j in corners)
 for n,(at,j) in enumerate(ordered):
  to,k=ordered[(n+1)%4];end=to if to>at else to+len(loop)
  axis=points[k]-points[j]
  for q in range(at,end+1):
   node=loop[q%len(loop)];t=np.clip((points[node]-points[j])@axis/(axis@axis),0,1)
   fixed[node]=np.array(anchors[j])*(1-t)+np.array(anchors[k])*t
 matrix=lil_matrix((len(points),len(points)));rhs=np.zeros((len(points),2))
 for j in range(len(points)):
  matrix[j,j]=1
  if j in fixed:rhs[j]=fixed[j]
  else:
   for k in neighbors[j]:matrix[j,k]=-1/len(neighbors[j])
 coords=np.clip(spsolve(matrix.tocsr(),rhs),0,1)
 assert np.isfinite(coords).all()
 meanz=[[],[]]
 for i in ids:
  half=int(np.mean(coords[[local[t] for t in fuv[i]],1])>.5)
  meanz[half].extend(v[faces[i],2])
 frontHalf=int(np.mean(meanz[1])>np.mean(meanz[0]))
 right=np.mean([v[j,0] for i in ids for j in faces[i]])<0
 first=2 if right else 4
 for i in ids:
  a=coords[[local[t] for t in fuv[i]]].copy()
  half=int(a[:,1].mean()>.5)
  a[:,1]=np.abs(a[:,1]-.5)*2
  canonical[i]=a.tolist();parts[i]=first+(0 if half==frontHalf else 1)
 print('Sleeve','wearer right' if right else 'wearer left','front half',frontHalf,'faces',len(ids),flush=True)
# Keep the authored geometry and its original UVs; only remove the pants.
used=sorted(set(j for f in faces for j in f));mapping={j:i for i,j in enumerate(used)}
verts=v[used].copy();bodyIds=set(j for ids in bodycharts for i in ids for j in faces[i]);body=v[sorted(bodyIds)]
sx=.52/(body[:,0].max()-body[:,0].min());sy=.71/(body[:,1].max()-body[:,1].min())
verts[:,0]=(verts[:,0]-(body[:,0].min()+body[:,0].max())/2)*sx
verts[:,1]=(verts[:,1]-(body[:,1].min()+body[:,1].max())/2)*sy
verts[:,2]=(verts[:,2]-(body[:,2].min()+body[:,2].max())/2)*sx*.70
data={'positions':verts.tolist(),'faces':[[mapping[j] for j in f] for f in faces],
      'uvOriginal':[uv[f].tolist() for f in fuv],'uvCanonical':canonical,'parts':parts,
      'ao':str(source_root/'male_casualsuit06_ao.png'),'normal':str(source_root/'male_casualsuit06_normal.png')}
(root/'prepared-shirt.json').write_text(json.dumps(data,separators=(',',':')))
print('Prepared shirt',len(verts),len(faces),flush=True)
